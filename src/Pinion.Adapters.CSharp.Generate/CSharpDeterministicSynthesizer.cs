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
internal sealed partial class CSharpDeterministicSynthesizer : IDisposable
{
    private const int MaxCandidatesPerParam = 6;
    private const int MaxRows = 12;
    private const int MaxObjectVariants = 8;
    private const int MaxSampleRows = 16;
    private const int PerTestTimeoutMs = 30000;
    private const string RowKeySep = "\u0001";

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>Render a symbol as C# regardless of its source language. A VB symbol's own
    /// <c>ToDisplayString</c> yields VB syntax (<c>Global.X</c>, <c>Task(Of T)</c>) — invalid in the
    /// emitted C# test — so every emission/comparison site formats through the C# renderer.</summary>
    internal static string Fq(ISymbol symbol) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.ToDisplayString(symbol, FullyQualified);

    /// <summary>C#-flavored default display — the form the emitter's string comparisons assume
    /// (e.g. <c>System.Threading.Tasks.Task&lt;TResult&gt;</c>).</summary>
    internal static string Def(ISymbol symbol) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.ToDisplayString(symbol);

    private readonly Action<string>? _log;
    private readonly bool _tryMsBuild;
    private readonly ConcurrentDictionary<string, Solution> _solutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Workspace> _workspaces = new();

    private readonly Dictionary<string, (string Name, INamedTypeSymbol Iface)> _stubs = new(StringComparer.Ordinal);

    public CSharpDeterministicSynthesizer(Action<string>? log = null, bool tryMsBuild = false)
    {
        _log = log;
        _tryMsBuild = tryMsBuild;
    }

    /// <summary>Dispose the MSBuild workspaces kept alive for their cached solutions.</summary>
    public void Dispose()
    {
        foreach (var ws in _workspaces) ws.Dispose();
        _workspaces.Clear();
        _solutions.Clear();
    }

    private sealed record Resolved(IMethodSymbol Method, BaseMethodDeclarationSyntax Decl, SemanticModel Model);

    internal sealed class Mined
    {
        public SortedSet<string> Strings { get; } = new(StringComparer.Ordinal);
        public SortedSet<long> Ints { get; } = new();
        public SortedSet<decimal> Decimals { get; } = new();
        public SortedSet<double> Doubles { get; } = new();
    }

    /// <summary>
    /// Sync facade for the CLI <c>--dry-run</c> path and unit tests. A console app has no
    /// <see cref="SynchronizationContext"/>, so blocking once here is safe; the async generate
    /// pipeline calls <see cref="SynthesizeAsync"/> directly.
    /// </summary>
    public string Synthesize(CodeUnit unit, string sourceRoot, CancellationToken ct) =>
        SynthesizeAsync(unit, sourceRoot, ct).GetAwaiter().GetResult();

    public async Task<string> SynthesizeAsync(CodeUnit unit, string sourceRoot, CancellationToken ct)
    {
        if (unit.FilePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
        {
            var vbSolution = await GetVbSolutionAsync(sourceRoot, ct).ConfigureAwait(false);
            var vb = await VbSynthesis.ResolveAsync(vbSolution, unit, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Could not resolve a symbol for {unit.DisplayName} (deterministic synthesis needs the source).");
            GuardSupported(unit, vb.Method);
            return Emit(unit, vb.Method, methodBody: null, VbSynthesis.MineConstants(vb.Body, vb.Model, ct));
        }

        var solution = await GetSolutionAsync(sourceRoot).ConfigureAwait(false);
        Resolved r = await ResolveAsync(solution, unit, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Could not resolve a symbol for {unit.DisplayName} (deterministic synthesis needs the source).");

        GuardSupported(unit, r.Method);
        SyntaxNode? body = (SyntaxNode?)r.Decl.Body ?? r.Decl.ExpressionBody;
        return Emit(unit, r.Method, body, MineConstants(r, ct));
    }

    /// <summary>Targets the deterministic generator can't characterize from another assembly.</summary>
    private static void GuardSupported(CodeUnit unit, IMethodSymbol method)
    {
        if (method.IsGenericMethod || method.ContainingType.IsGenericType)
            throw new NotSupportedException("generic methods/types aren't supported by the deterministic generator yet — try --provider anthropic.");

        if (method.DeclaredAccessibility != Accessibility.Public)
            throw new NotSupportedException($"method is not public ({unit.DisplayName} is {method.DeclaredAccessibility.ToString().ToLowerInvariant()}).");
        if (method.ContainingType.DeclaredAccessibility != Accessibility.Public)
            throw new NotSupportedException($"containing type is not public ({method.ContainingType.Name} is {method.ContainingType.DeclaredAccessibility.ToString().ToLowerInvariant()}).");
    }

    /// <summary>VB solution, cached per source root (parallel to <see cref="GetSolutionAsync"/>).</summary>
    private async Task<Solution> GetVbSolutionAsync(string sourceRoot, CancellationToken ct)
    {
        string key = "vb::" + Path.GetFullPath(sourceRoot);
        if (_solutions.TryGetValue(key, out var cached)) return cached;
        var (solution, workspace) = await Pinion.Adapters.VisualBasic.VbGenerateLoader
            .LoadAsync(sourceRoot, _tryMsBuild, _log, ct).ConfigureAwait(false);
        _workspaces.Add(workspace);
        return _solutions.GetOrAdd(key, solution);
    }

    /// <summary>Cached per source root. A first-writer-wins race just leaves an extra workspace for Dispose.</summary>
    private async Task<Solution> GetSolutionAsync(string sourceRoot)
    {
        string key = Path.GetFullPath(sourceRoot);
        if (_solutions.TryGetValue(key, out var cached)) return cached;
        var loaded = await LoadWithReferencesAsync(sourceRoot).ConfigureAwait(false);
        return _solutions.GetOrAdd(key, loaded);
    }

    /// <summary>
    /// Load the project via MSBuild so the compilation has the project's RESTORED references
    /// (framework + NuGet) — that's what lets the synthesizer construct real values instead of
    /// `default!`. Falls back to a runtime-refs-only source scan when MSBuild is unavailable
    /// (no SDK match, global.json mismatch) — in that case external types won't resolve.
    /// </summary>
    private async Task<Solution> LoadWithReferencesAsync(string sourceRoot)
    {
        if (_tryMsBuild)
        try
        {
            var ws = MSBuildWorkspace.Create();
            ws.RegisterWorkspaceFailedHandler(e => _log?.Invoke($"[roslyn] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
            string target = CSharpAdapter.ResolveInputPath(sourceRoot);
            Solution sol = await CSharpAdapter.OpenViaMSBuildAsync(ws, target, CancellationToken.None).ConfigureAwait(false);

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

        var scanned = SourceScanLoader.Load(sourceRoot, _log);
        _workspaces.Add(scanned.Workspace);
        return scanned;
    }

    private static async Task<Resolved?> ResolveAsync(Solution solution, CodeUnit unit, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var doc = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, unit.FilePath, StringComparison.OrdinalIgnoreCase));
            if (doc is null) continue;

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (model is null || root is null) continue;

            foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == unit.StartLine
                    && model.GetDeclaredSymbol(decl, ct) is IMethodSymbol m
                    && NameMatches(m, unit.SimpleName))
                {
                    return new Resolved(m, decl, model);
                }
            }
        }
        return null;
    }

    /// <summary>Match a resolved symbol to the unit's simple name. A constructor's symbol name is ".ctor"
    /// while the unit's display/simple name uses "ctor" — bridge that so constructors resolve.</summary>
    private static bool NameMatches(IMethodSymbol m, string simpleName) =>
        m.Name == simpleName
        || (m.MethodKind == MethodKind.Constructor && simpleName is "ctor" or ".ctor");

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
                case CaseSwitchLabelSyntax label:
                    Take(label.Value);
                    break;
                case ConstantPatternSyntax cp:
                    Take(cp.Expression);
                    break;
                case RelationalPatternSyntax rp:
                    Take(rp.Expression);
                    break;
                case InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax ma && IsPredicateCall(ma.Name.Identifier.ValueText):
                    foreach (var a in inv.ArgumentList.Arguments) Take(a.Expression);
                    break;
            }
        }
        return mined;
    }

    private static bool IsPredicateCall(string name) => name is
        "StartsWith" or "EndsWith" or "Contains" or "Equals" or "IndexOf" or "IsMatch" or "Match";

    private static bool IsComparison(SyntaxKind kind) => kind is
        SyntaxKind.LessThanExpression or SyntaxKind.GreaterThanExpression
        or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanOrEqualExpression
        or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    /// <summary>Emit the C# characterization test. Symbol-driven, so it serves BOTH languages: for a VB
    /// target <paramref name="methodBody"/> is null (no C# body to run the string-guard solver on) and
    /// the emitted C# test calls the VB assembly directly.</summary>
    private string Emit(CodeUnit unit, IMethodSymbol method, SyntaxNode? methodBody, Mined mined)
    {
        _stubs.Clear();
        string type = Fq(method.ContainingType);
        string methodName = method.Name;
        bool isStatic = method.IsStatic;
        bool isCtor = method.MethodKind == MethodKind.Constructor;
        var ret = isCtor ? new ReturnShape(IsAsync: false, IsVoidLike: false, IsRefLike: false) : EffectiveReturn(method);
        string testMethodName = isCtor ? "Constructor" : SafeId(methodName);

        string className = Pinion.Engine.Reporting.CharacterizationNaming.TestClassName(unit.DisplayName, unit.Id);

        var perParam = method.Parameters
            .Select(p => p.RefKind == RefKind.Out ? new List<string> { "out _" }
                       : p.Type.SpecialType == SpecialType.System_String ? StringCandidates(p, methodBody, mined)
                       : Candidates(p.Type, mined))
            .ToList();
        var rows = BuildRows(perParam);
        AppendSampleRows(rows, method, mined, unit.Id, perParam);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Pinion.Engine.Reporting.CharacterizationFormat.Stamp);
        sb.AppendLine("#nullable enable annotations");
        sb.AppendLine("#nullable disable warnings");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using VerifyTests;");
        sb.AppendLine("using VerifyXunit;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine("using static VerifyXunit.Verifier;");
        sb.AppendLine();
        sb.AppendLine("namespace Pinion.Generated;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    [Fact(Timeout = {PerTestTimeoutMs})]");
        sb.AppendLine($"    public async Task {testMethodName}_characterization()");
        sb.AppendLine("    {");
        sb.AppendLine("        var entries = new List<object>();");
        if (!isStatic && !isCtor) sb.AppendLine($"        var sut = {BuildSut(method.ContainingType)};");
        sb.AppendLine();

        foreach (var row in rows)
        {
            var call = BuildCall(method, row);
            string invoke = isCtor
                ? "new " + type + "(" + string.Join(", ", call.Args) + ")"
                : (isStatic ? type : "sut") + "." + methodName + "(" + string.Join(", ", call.Args) + ")";
            if (ret.IsAsync) invoke = "await " + invoke;
            string desc = Escape("(" + string.Join(", ", call.Display) + ")");

            string captures = string.Concat(call.Captures.Select(c => $", {c.Field} = (object?)({c.Var})"));

            sb.AppendLine("        {");
            foreach (var d in call.Pre) sb.AppendLine($"            {d}");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            if (ret.IsVoidLike)
            {
                sb.AppendLine($"                {invoke};");
                sb.AppendLine($"                entries.Add(new {{ Input = \"{desc}\", Outcome = (object?)\"completed (no return value)\"{captures} }});");
            }
            else
            {
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

        sb.AppendLine("        var settings = new VerifySettings();");
        sb.AppendLine("        settings.IgnoreMembersThatThrow<Exception>();");
        sb.AppendLine("        await Verify(entries, settings);");
        sb.AppendLine("    }");

        foreach (var (name, iface) in _stubs.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.AppendLine();
            EmitStub(sb, name, iface);
        }

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
                    pre.Add($"{Fq(p.Type)} {ov} = default!;");
                    args.Add($"out {ov}");
                    display.Add("out");
                    captures.Add((CaptureField(p.Name), ov));
                    break;
                case RefKind.Ref:
                    string rv = $"__r{i}";
                    pre.Add($"{Fq(p.Type)} {rv} = {row[i]};");
                    args.Add($"ref {rv}");
                    display.Add(row[i]);
                    captures.Add((CaptureField(p.Name), rv));
                    break;
                default:
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
        string def = Def(rt.OriginalDefinition);

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
        var seen = new HashSet<string> { string.Join(RowKeySep, baseRow) };

        for (int i = 0; i < perParam.Count && rows.Count < MaxRows; i++)
        {
            for (int j = 1; j < perParam[i].Count && rows.Count < MaxRows; j++)
            {
                var row = new List<string>(baseRow) { [i] = perParam[i][j] };
                if (seen.Add(string.Join(RowKeySep, row))) rows.Add(row);
            }
        }
        return rows;
    }

}
