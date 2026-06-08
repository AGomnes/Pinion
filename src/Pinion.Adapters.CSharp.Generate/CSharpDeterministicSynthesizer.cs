using System.Collections.Concurrent;
using System.Globalization;
using CsCheck;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Model;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>
/// Generates a characterization test for a method WITHOUT any AI — deterministically, from
/// Roslyn's semantic model. It reads real parameter/return types, constructs concrete argument
/// values, and — crucially — mines the constants the method actually compares against (switch
/// cases, literal thresholds) plus boundary neighbours, so the generated inputs reach real
/// branches instead of recording one shallow path.
/// </summary>
/// <remarks>
/// Deterministic by design: candidate values are sorted, so identical source in → identical test
/// + identical golden master. Nothing leaves the machine.
/// </remarks>
internal sealed class CSharpDeterministicSynthesizer
{
    private const int MaxCandidatesPerParam = 6;
    private const int MaxRows = 12;
    // How many variants of a constructed parameter object to synthesise (base + field variations).
    private const int MaxObjectVariants = 8;
    // Deterministic joint-random sample rows added for numeric-input methods (property-based tier).
    private const int MaxSampleRows = 16;
    // Per-test cap so one hanging/infinite-loop method fails just its own test, not the whole batch.
    private const int PerTestTimeoutMs = 30000;

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private readonly Action<string>? _log;
    private readonly bool _tryMsBuild;
    private readonly ConcurrentDictionary<string, Solution> _solutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MSBuildWorkspace> _workspaces = new(); // kept alive while their solutions are in use

    public CSharpDeterministicSynthesizer(Action<string>? log = null, bool tryMsBuild = false)
    {
        _log = log;
        _tryMsBuild = tryMsBuild;
    }

    private sealed record Resolved(IMethodSymbol Method, MethodDeclarationSyntax Decl, SemanticModel Model);

    private sealed class Mined
    {
        public SortedSet<string> Strings { get; } = new(StringComparer.Ordinal);
        public SortedSet<long> Ints { get; } = new();
        public SortedSet<decimal> Decimals { get; } = new();
        public SortedSet<double> Doubles { get; } = new();
    }

    public string Synthesize(CodeUnit unit, string sourceRoot, CancellationToken ct)
    {
        var solution = _solutions.GetOrAdd(Path.GetFullPath(sourceRoot), _ => LoadWithReferences(sourceRoot));
        Resolved r = Resolve(solution, unit, ct)
            ?? throw new InvalidOperationException($"Could not resolve a symbol for {unit.DisplayName} (deterministic synthesis needs the source).");

        if (r.Method.IsGenericMethod || r.Method.ContainingType.IsGenericType)
            throw new NotSupportedException("generic methods/types aren't supported by the deterministic generator yet — try --provider anthropic.");

        // A test in a separate assembly can only call public members of public types.
        if (r.Method.DeclaredAccessibility != Accessibility.Public)
            throw new NotSupportedException($"method is not public ({unit.DisplayName} is {r.Method.DeclaredAccessibility.ToString().ToLowerInvariant()}).");
        if (r.Method.ContainingType.DeclaredAccessibility != Accessibility.Public)
            throw new NotSupportedException($"containing type is not public ({r.Method.ContainingType.Name} is {r.Method.ContainingType.DeclaredAccessibility.ToString().ToLowerInvariant()}).");

        return Emit(unit, r, MineConstants(r, ct));
    }

    /// <summary>
    /// Load the project via MSBuild so the compilation has the project's RESTORED references
    /// (framework + NuGet) — that's what lets the synthesizer construct real values instead of
    /// `default!`. Falls back to a runtime-refs-only source scan when MSBuild is unavailable
    /// (no SDK match, global.json mismatch) — in that case external types won't resolve.
    /// </summary>
    private Solution LoadWithReferences(string sourceRoot)
    {
        // Only attempt MSBuild when the host has registered it (the CLI does; test hosts don't).
        if (_tryMsBuild)
        try
        {
            var ws = MSBuildWorkspace.Create();
            ws.RegisterWorkspaceFailedHandler(e => _log?.Invoke($"[roslyn] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
            string target = CSharpAdapter.ResolveInputPath(sourceRoot);
            Solution sol = CSharpAdapter.OpenViaMSBuildAsync(ws, target, CancellationToken.None).GetAwaiter().GetResult();

            if (CSharpAdapter.HasCSharpDocuments(sol))
            {
                _workspaces.Add(ws);
                _log?.Invoke("[generate] resolved project references via MSBuild (full type resolution).");
                return sol;
            }
            ws.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[generate] MSBuild unavailable ({ex.GetType().Name}); scanning source — types from referenced packages may not resolve.");
        }

        return SourceScanLoader.Load(sourceRoot, _log);
    }

    private static Resolved? Resolve(Solution solution, CodeUnit unit, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var doc = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, unit.FilePath, StringComparison.OrdinalIgnoreCase));
            if (doc is null) continue;

            var model = doc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
            var root = doc.GetSyntaxRootAsync(ct).GetAwaiter().GetResult();
            if (model is null || root is null) continue;

            foreach (var decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == unit.StartLine
                    && model.GetDeclaredSymbol(decl, ct) is IMethodSymbol m
                    && m.Name == LastSegmentName(unit))
                {
                    return new Resolved(m, decl, model);
                }
            }
        }
        return null;
    }

    private static string LastSegmentName(CodeUnit unit)
    {
        int dot = unit.DisplayName.LastIndexOf('.');
        return dot >= 0 ? unit.DisplayName[(dot + 1)..] : unit.DisplayName;
    }

    /// <summary>
    /// Collect the constants the method uses to DECIDE — operands of comparisons (&lt; &gt; == …),
    /// switch-case labels, and pattern constants. These are the branch boundaries; multiplier/format
    /// constants (e.g. a `* 0.02m` rate) are deliberately ignored because they don't gate behaviour.
    /// </summary>
    private static Mined MineConstants(Resolved r, CancellationToken ct)
    {
        var mined = new Mined();
        SyntaxNode? body = (SyntaxNode?)r.Decl.Body ?? r.Decl.ExpressionBody;
        if (body is null) return mined;

        void Take(SyntaxNode? node)
        {
            if (node is null) return;
            var cv = r.Model.GetConstantValue(node, ct);
            if (!cv.HasValue) return;
            switch (cv.Value)
            {
                case string s: mined.Strings.Add(s); break;
                case int i: mined.Ints.Add(i); break;
                case long l: mined.Ints.Add(l); break;
                case short sh: mined.Ints.Add(sh); break;
                case byte b: mined.Ints.Add(b); break;
                case decimal d: mined.Decimals.Add(d); break;
                case double db: mined.Doubles.Add(db); break;
                case float f: mined.Doubles.Add(f); break;
            }
        }

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case BinaryExpressionSyntax be when IsComparison(be.Kind()):
                    Take(be.Left); Take(be.Right);
                    break;
                case CaseSwitchLabelSyntax label:       // case "NO":
                    Take(label.Value);
                    break;
                case ConstantPatternSyntax cp:          // is "NO", case 0 =>
                    Take(cp.Expression);
                    break;
                case RelationalPatternSyntax rp:        // > 10000
                    Take(rp.Expression);
                    break;
                case InvocationExpressionSyntax inv      // s.StartsWith("CLEARANCE"), coupon.Contains("SAVE")
                    when inv.Expression is MemberAccessExpressionSyntax ma && IsPredicateCall(ma.Name.Identifier.ValueText):
                    foreach (var a in inv.ArgumentList.Arguments) Take(a.Expression);
                    break;
            }
        }
        return mined;
    }

    // Calls whose string argument gates behaviour — mine those literals so inputs can satisfy the guard.
    private static bool IsPredicateCall(string name) => name is
        "StartsWith" or "EndsWith" or "Contains" or "Equals" or "IndexOf" or "IsMatch" or "Match";

    private static bool IsComparison(SyntaxKind kind) => kind is
        SyntaxKind.LessThanExpression or SyntaxKind.GreaterThanExpression
        or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanOrEqualExpression
        or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    private string Emit(CodeUnit unit, Resolved r, Mined mined)
    {
        IMethodSymbol method = r.Method;
        string type = method.ContainingType.ToDisplayString(FullyQualified);
        string methodName = method.Name;
        bool isStatic = method.IsStatic;
        var ret = EffectiveReturn(method);

        // Include a short id hash so overloads (same DisplayName) don't collide on class name.
        string className = $"{SafeId(unit.DisplayName)}_{ShortHash(unit.Id)}_CharacterizationTests";

        // out params carry no input value; others vary across their candidates.
        var perParam = method.Parameters
            .Select(p => p.RefKind == RefKind.Out ? new List<string> { "out _" } : Candidates(p.Type, mined))
            .ToList();
        var rows = BuildRows(perParam);
        // Property-based tier: for methods with numeric inputs, add deterministic joint-random rows.
        // One-hot rows hold the other params at a fixed base, which can be degenerate (isExempt=true,
        // daysLate=-1) and starve whole code paths; joint sampling sets every param at once.
        AppendSampleRows(rows, method, mined, unit.Id, perParam);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using VerifyXunit;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine("using static VerifyXunit.Verifier;");
        sb.AppendLine();
        sb.AppendLine("namespace Pinion.Generated;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    [Fact(Timeout = {PerTestTimeoutMs})]");
        sb.AppendLine($"    public async Task {SafeId(methodName)}_characterization()");
        sb.AppendLine("    {");
        sb.AppendLine("        var entries = new List<object>();");
        if (!isStatic) sb.AppendLine($"        var sut = {BuildSut(method.ContainingType)};");
        sb.AppendLine();

        foreach (var row in rows)
        {
            var call = BuildCall(method, row);
            string invoke = (isStatic ? type : "sut") + "." + methodName + "(" + string.Join(", ", call.Args) + ")";
            if (ret.IsAsync) invoke = "await " + invoke;
            string desc = Escape("(" + string.Join(", ", call.Display) + ")");

            // Capture the return value AND the final value of every out/ref parameter — that's
            // where the behaviour of TryX/accumulator methods actually lives.
            string captures = string.Concat(call.Captures.Select(c => $", {c.Field} = (object?)({c.Var})"));

            sb.AppendLine("        {");
            foreach (var d in call.Pre) sb.AppendLine($"            {d}"); // declared before try so the catch can read them
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            if (ret.IsVoidLike)
            {
                sb.AppendLine($"                {invoke};");
                sb.AppendLine($"                entries.Add(new {{ Input = \"{desc}\", Outcome = (object?)\"completed (no return value)\"{captures} }});");
            }
            else
            {
                // Ref-struct results (e.g. Span<T>) can't be boxed — record their text form.
                string captured = ret.IsRefLike ? "result.ToString()" : "result";
                sb.AppendLine($"                var result = {invoke};");
                sb.AppendLine($"                entries.Add(new {{ Input = \"{desc}\", Outcome = (object?)({captured}){captures} }});");
            }
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex)");
            sb.AppendLine("            {");
            sb.AppendLine($"                entries.Add(new {{ Input = \"{desc}\", Outcome = (object?)(ex.GetType().Name + \": \" + ex.Message){captures} }});");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        await Verify(entries);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private readonly record struct CallPlan(
        List<string> Pre, List<string> Args, List<string> Display, List<(string Field, string Var)> Captures);

    /// <summary>
    /// Build the call: pre-declarations (out/ref temps), the argument expressions, a readable display,
    /// and the out/ref values to capture in the snapshot.
    /// </summary>
    private static CallPlan BuildCall(IMethodSymbol method, List<string> row)
    {
        var pre = new List<string>();
        var args = new List<string>();
        var display = new List<string>();
        var captures = new List<(string, string)>();

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            switch (p.RefKind)
            {
                case RefKind.Out:
                    string ov = $"__o{i}";
                    pre.Add($"{p.Type.ToDisplayString(FullyQualified)} {ov} = default!;");
                    args.Add($"out {ov}");
                    display.Add("out");
                    captures.Add((CaptureField(p.Name), ov));
                    break;
                case RefKind.Ref:
                    string rv = $"__r{i}";
                    pre.Add($"var {rv} = {row[i]};");
                    args.Add($"ref {rv}");
                    display.Add(row[i]);
                    captures.Add((CaptureField(p.Name), rv));
                    break;
                default: // None / In (an `in` argument can be passed by value)
                    args.Add(row[i]);
                    display.Add(row[i]);
                    break;
            }
        }
        return new CallPlan(pre, args, display, captures);
    }

    /// <summary>A valid, non-colliding anonymous-object field name for an out/ref capture.</summary>
    private static string CaptureField(string paramName)
    {
        string id = System.Text.RegularExpressions.Regex.Replace(paramName, @"\W", "_");
        return id is "Input" or "Outcome" or "" ? "out_" + id : id;
    }

    private readonly record struct ReturnShape(bool IsAsync, bool IsVoidLike, bool IsRefLike);

    private static ReturnShape EffectiveReturn(IMethodSymbol method)
    {
        if (method.ReturnsVoid) return new ReturnShape(false, true, false);

        var rt = method.ReturnType;
        string def = rt.OriginalDefinition.ToDisplayString();

        if (def is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
            return new ReturnShape(IsAsync: true, IsVoidLike: true, IsRefLike: false);

        if (rt is INamedTypeSymbol { IsGenericType: true } n
            && def is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
        {
            var inner = n.TypeArguments[0];
            return new ReturnShape(IsAsync: true, IsVoidLike: false, IsRefLike: inner.IsRefLikeType);
        }

        return new ReturnShape(IsAsync: false, IsVoidLike: false, IsRefLike: rt.IsRefLikeType);
    }

    private static string ShortHash(string s)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 3).ToLowerInvariant();
    }

    /// <summary>One-hot rows: vary each parameter across its candidates while holding the rest at their first value.</summary>
    private static List<List<string>> BuildRows(List<List<string>> perParam)
    {
        if (perParam.Count == 0) return new() { new List<string>() };

        var baseRow = perParam.Select(c => c[0]).ToList();
        var rows = new List<List<string>> { baseRow };
        var seen = new HashSet<string> { string.Join("", baseRow) };

        for (int i = 0; i < perParam.Count && rows.Count < MaxRows; i++)
        {
            for (int j = 1; j < perParam[i].Count && rows.Count < MaxRows; j++)
            {
                var row = new List<string>(baseRow) { [i] = perParam[i][j] };
                if (seen.Add(string.Join("", row))) rows.Add(row);
            }
        }
        return rows;
    }

    private static readonly SpecialType[] NativeNumeric =
    {
        SpecialType.System_Int32, SpecialType.System_Int64,
        SpecialType.System_Decimal, SpecialType.System_Double,
    };

    /// <summary>
    /// Property-based tier (CsCheck): for methods that take a numeric input, append deterministic
    /// joint-random rows — every parameter sampled at once, in a range derived from the mined
    /// constants — so arithmetic/loop behaviour that fixed one-hot rows can't distinguish gets pinned.
    /// Reproducible: the PCG is seeded from the method id, and values are baked into the test as
    /// literals (the generated test takes no runtime dependency on CsCheck).
    /// </summary>
    private void AppendSampleRows(List<List<string>> rows, IMethodSymbol method, Mined mined, string seedKey, List<List<string>> perParam)
    {
        if (!method.Parameters.Any(p => NativeNumeric.Contains(Unwrap(p.Type).SpecialType))) return;

        var gens = new List<Gen<string>>();
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            gens.Add(p.RefKind == RefKind.Out ? Gen.Const("out _") : LiteralGen(Unwrap(p.Type), mined, perParam[i]));
        }

        var (stream, state) = SeedFrom(seedKey);
        var pcg = new PCG(stream, state); // two-arg ctor = fully seeded (single-arg seeds state from time)
        var seen = new HashSet<string>(rows.Select(r => string.Join("", r)));
        for (int k = 0; k < MaxSampleRows; k++)
        {
            var row = gens.Select(g => g.Generate(pcg, null, out _)).ToList();
            if (seen.Add(string.Join("", row))) rows.Add(row);
        }
    }

    /// <summary>A CsCheck generator that yields a compilable source literal for a parameter.</summary>
    private Gen<string> LiteralGen(ITypeSymbol t, Mined mined, List<string> candidates)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Decimal:
            {
                var (lo, hi) = DecimalRange(mined);
                return Gen.Decimal[lo, hi].Select(v => FmtDecimal(decimal.Round(v, 2)));
            }
            case SpecialType.System_Double:
            {
                var (lo, hi) = DoubleRange(mined);
                return Gen.Double[lo, hi].Select(v => FmtDouble(Math.Round(v, 2)));
            }
            case SpecialType.System_Int32 or SpecialType.System_Int64:
            {
                var (lo, hi) = IntRange(mined);
                return Gen.Int[lo, hi].Select(v => v.ToString(CultureInfo.InvariantCulture));
            }
            case SpecialType.System_Boolean:
                return Gen.Bool.Select(v => v ? "true" : "false");
            default:
                // string / enum / char / object / smaller ints: pick among the already-safe candidates.
                return Gen.OneOfConst(candidates.ToArray());
        }
    }

    private static (uint Stream, ulong State) SeedFrom(string s)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return (BitConverter.ToUInt32(h, 0) | 1u, BitConverter.ToUInt64(h, 4));
    }

    // Ranges that span the mined branch boundaries on both sides (and always include positive values
    // so guards like `amount > 0` / `daysLate > 0` are cleared by some samples).
    private static (int, int) IntRange(Mined mined)
    {
        var xs = mined.Ints.Where(v => v is > int.MinValue and < int.MaxValue).Select(v => (int)v).ToList();
        int mx = Math.Min(xs.DefaultIfEmpty(0).Max(), 100_000);
        int lo = Math.Min(0, xs.DefaultIfEmpty(0).Min()) - 5;
        return (lo, Math.Max(1000, mx * 2 + 100));
    }

    private static (decimal, decimal) DecimalRange(Mined mined)
    {
        var xs = mined.Decimals.Concat(mined.Ints.Select(i => (decimal)i)).ToList();
        decimal mx = Math.Min(xs.DefaultIfEmpty(0m).Max(), 1_000_000m);
        return (-10m, Math.Max(10_000m, mx * 2m + 100m));
    }

    private static (double, double) DoubleRange(Mined mined)
    {
        double mx = Math.Min(mined.Doubles.DefaultIfEmpty(0).Max(), 1_000_000);
        return (-10, Math.Max(10_000, mx * 2 + 100));
    }

    /// <summary>Candidate source-literals for a parameter: mined constants + boundaries + a small catalog.</summary>
    private List<string> Candidates(ITypeSymbol type, Mined mined)
    {
        var t = Unwrap(type);
        var values = new List<string>();

        switch (t.SpecialType)
        {
            case SpecialType.System_String:
                // A "rich" alphanumeric string long enough to clear length/letter/digit guards, so
                // inputs actually get PAST validation into the real logic (the #1 coverage gap).
                // Length derives from the method's own length-comparison constants.
                values.Add(Quote(RichString(GuardLength(mined))));
                values.Add("\"12345\""); // numeric — reaches int.TryParse/Parse success branches
                foreach (var s in mined.Strings) values.Add(Quote(s));
                values.Add("\"\""); values.Add("null");
                break;

            case SpecialType.System_Int32 or SpecialType.System_Int64
                or SpecialType.System_Int16 or SpecialType.System_Byte:
                foreach (var v in mined.Ints) AddNeighbours(values, v, n => n.ToString(CultureInfo.InvariantCulture));
                values.Add("0"); values.Add("1"); values.Add("-1");
                break;

            case SpecialType.System_Decimal:
                foreach (var v in mined.Decimals) AddNeighbours(values, v, FmtDecimal);
                foreach (var v in mined.Ints) values.Add(FmtDecimal(v));
                values.Add("0m"); values.Add("1m"); values.Add("-1m");
                break;

            case SpecialType.System_Double or SpecialType.System_Single:
                foreach (var v in mined.Doubles) AddNeighbours(values, v, FmtDouble);
                values.Add("0d"); values.Add("1d");
                break;

            case SpecialType.System_Boolean:
                values.Add("true"); values.Add("false");
                break;

            case SpecialType.System_Char:
                values.Add("'a'"); values.Add("'0'");
                break;

            default:
                if (t.TypeKind == TypeKind.Enum)
                {
                    values.AddRange(EnumMembers(t)); // real enum members, not default(0)
                }
                else if (ElementType(t) is { } elem)
                {
                    // Collection: one-element arrays, each holding a different constructed element, so
                    // branches that test the element's FIELDS (e.g. line.Quantity >= 100) get reached.
                    foreach (var e in ElementCandidates(elem, mined)) values.Add($"new[] {{ {e} }}");
                }
                else if (t is INamedTypeSymbol nt && AccessibleCtor(nt) is not null)
                {
                    values.AddRange(ConstructionVariants(nt, mined));
                }
                if (values.Count == 0) values.Add(BuildValue(t, 0)); // default fallback
                break;
        }

        return values.Distinct().Take(MaxCandidatesPerParam).ToList();
    }

    /// <summary>A handful of element values for a collection — constructed variants if the element is an object.</summary>
    private List<string> ElementCandidates(ITypeSymbol element, Mined mined)
    {
        var e = Unwrap(element);
        if (IsSimple(e)) return Candidates(e, mined);
        if (e is INamedTypeSymbol nt && AccessibleCtor(nt) is not null) return ConstructionVariants(nt, mined);
        return new() { BuildValue(e, 1) };
    }

    /// <summary>
    /// A non-degenerate base construction plus one-hot variations of each ctor argument across the
    /// mined constants — so object-field branches (quantity tiers, clearance SKUs, …) get exercised.
    /// </summary>
    private List<string> ConstructionVariants(INamedTypeSymbol type, Mined mined)
    {
        string fq = type.ToDisplayString(FullyQualified);
        var ctor = AccessibleCtor(type);
        if (ctor is null) return new() { $"default({fq})!" };
        if (ctor.Parameters.Length == 0) return new() { $"new {fq}()" };

        var perArg = ctor.Parameters.Select(p => CtorArgCandidates(p.Type, mined)).ToList();
        var baseArgs = perArg.Select(c => c[0]).ToList();

        var variants = new List<string> { $"new {fq}({string.Join(", ", baseArgs)})" };
        var seen = new HashSet<string> { string.Join("|", baseArgs) };
        // Round-robin across the ctor args (not first-param-first) so EVERY field gets varied within
        // the budget — otherwise one many-valued field (e.g. a string Sku) starves the others.
        for (int j = 1; variants.Count < MaxObjectVariants; j++)
        {
            bool progressed = false;
            for (int i = 0; i < perArg.Count && variants.Count < MaxObjectVariants; i++)
            {
                if (j >= perArg[i].Count) continue;
                progressed = true;
                var args = new List<string>(baseArgs) { [i] = perArg[i][j] };
                if (seen.Add(string.Join("|", args))) variants.Add($"new {fq}({string.Join(", ", args)})");
            }
            if (!progressed) break;
        }
        return variants;
    }

    /// <summary>
    /// Constructor-argument candidates, ordered NON-degenerate-first (a positive/rich value, so the
    /// base object actually exercises code) with the mined branch constants reachable as variations.
    /// </summary>
    private List<string> CtorArgCandidates(ITypeSymbol type, Mined mined)
    {
        var t = Unwrap(type);
        var v = new List<string>();
        switch (t.SpecialType)
        {
            case SpecialType.System_String:
                v.Add(Quote(RichString(GuardLength(mined))));
                foreach (var s in mined.Strings) v.Add(Quote(s));
                v.Add("\"\""); v.Add("null");
                break;
            case SpecialType.System_Int32 or SpecialType.System_Int64
                or SpecialType.System_Int16 or SpecialType.System_Byte:
                v.Add("1");
                foreach (var i in mined.Ints) v.Add(i.ToString(CultureInfo.InvariantCulture));
                v.Add("0"); v.Add("-1");
                break;
            case SpecialType.System_Decimal:
                v.Add("1m");
                foreach (var d in mined.Decimals) v.Add(FmtDecimal(d));
                foreach (var i in mined.Ints) v.Add(FmtDecimal(i));
                v.Add("0m"); v.Add("-1m");
                break;
            case SpecialType.System_Double or SpecialType.System_Single:
                v.Add("1d");
                foreach (var d in mined.Doubles) v.Add(FmtDouble(d));
                v.Add("0d");
                break;
            case SpecialType.System_Boolean: v.Add("true"); v.Add("false"); break;
            case SpecialType.System_Char: v.Add("'a'"); v.Add("'0'"); break;
            default:
                if (t.TypeKind == TypeKind.Enum) v.AddRange(EnumMembers(t));
                else v.Add(BuildValue(t, 1)); // nested object/collection: single, depth-bounded
                break;
        }
        return v.Distinct().Take(MaxCandidatesPerParam).ToList();
    }

    private static bool IsSimple(ITypeSymbol t) =>
        t.SpecialType is SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Char
            or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
            or SpecialType.System_Byte or SpecialType.System_Decimal or SpecialType.System_Double
            or SpecialType.System_Single
        || t.TypeKind == TypeKind.Enum;

    private static void AddNeighbours(List<string> into, long v, Func<long, string> fmt)
    {
        into.Add(fmt(v - 1)); into.Add(fmt(v)); into.Add(fmt(v + 1));
    }

    private static void AddNeighbours(List<string> into, decimal v, Func<decimal, string> fmt)
    {
        into.Add(fmt(v - 1)); into.Add(fmt(v)); into.Add(fmt(v + 1));
    }

    private static void AddNeighbours(List<string> into, double v, Func<double, string> fmt)
    {
        into.Add(fmt(v - 1)); into.Add(fmt(v)); into.Add(fmt(v + 1));
    }

    // A length that clears a typical lower-bound length guard: the smallest plausible length
    // constant the method compares against (8..256), else a sensible default. Capped for readability.
    private static int GuardLength(Mined mined) =>
        Math.Min(64, mined.Ints.Where(i => i is >= 8 and <= 256).Select(i => (int)i).DefaultIfEmpty(20).Min());

    /// <summary>An alphanumeric string of the given length (letters + digits, never starts with '-').</summary>
    private static string RichString(int len)
    {
        var sb = new System.Text.StringBuilder(len);
        while (sb.Length < len) sb.Append("Ab1");
        return sb.ToString()[..len];
    }

    private static List<string> EnumMembers(ITypeSymbol enumType) =>
        enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst)
            .Select(f => enumType.ToDisplayString(FullyQualified) + "." + f.Name)
            .Take(MaxCandidatesPerParam).ToList();

    private static string FmtDecimal(decimal v) => v.ToString(CultureInfo.InvariantCulture) + "m";
    private static string FmtDouble(double v) => v.ToString("R", CultureInfo.InvariantCulture) + "d";

    private string BuildSut(INamedTypeSymbol type)
    {
        string fq = type.ToDisplayString(FullyQualified);
        var ctor = AccessibleCtor(type);
        if (ctor is null) return $"default({fq})!";
        var args = ctor.Parameters.Select(p => BuildValue(p.Type, 1));
        return $"new {fq}({string.Join(", ", args)})";
    }

    private string BuildValue(ITypeSymbol type, int depth)
    {
        type = Unwrap(type);

        var first = Unwrap(type).SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
                or SpecialType.System_Byte => "0",
            SpecialType.System_Decimal => "0m",
            SpecialType.System_Double or SpecialType.System_Single => "0d",
            SpecialType.System_Boolean => "true",
            SpecialType.System_String => "\"\"",
            SpecialType.System_Char => "'a'",
            _ => (string?)null,
        };
        if (first is not null) return first;

        if (type.TypeKind == TypeKind.Enum)
            return EnumMembers(type).FirstOrDefault() ?? $"default({type.ToDisplayString(FullyQualified)})";

        if (depth <= 3)
        {
            var element = ElementType(type);
            if (element is not null)
                return $"new[] {{ {BuildValue(element, depth + 1)} }}";

            if (type is INamedTypeSymbol named && AccessibleCtor(named) is { } ctor)
            {
                var args = ctor.Parameters.Select(p => BuildValue(p.Type, depth + 1));
                return $"new {named.ToDisplayString(FullyQualified)}({string.Join(", ", args)})";
            }
        }

        return $"default({type.ToDisplayString(FullyQualified)})!";
    }

    private static IMethodSymbol? AccessibleCtor(INamedTypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Interface or TypeKind.Enum || type.IsAbstract) return null;
        return type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderBy(c => c.Parameters.Length)
            .FirstOrDefault();
    }

    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            ? n.TypeArguments[0]
            : type;

    private static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is IArrayTypeSymbol arr) return arr.ElementType;

        if (type is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1
            && n.OriginalDefinition.ToDisplayString().StartsWith("System.Collections.Generic.", StringComparison.Ordinal))
            return n.TypeArguments[0];

        var ienum = type.AllInterfaces.FirstOrDefault(i => i.Name == "IEnumerable" && i.TypeArguments.Length == 1);
        return ienum?.TypeArguments.FirstOrDefault();
    }

    private static string SafeId(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\W", "_");
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Quote(string s) => "\"" + Escape(s) + "\"";
}
