using System.Globalization;
using CsCheck;
using Microsoft.CodeAnalysis;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>
/// Input synthesis for <see cref="CSharpDeterministicSynthesizer"/>: candidate value generation,
/// object/collection construction from mined constants, and the CsCheck property-based sampling tier.
/// </summary>
internal sealed partial class CSharpDeterministicSynthesizer
{
    private static readonly SpecialType[] NativeNumeric =
    {
        SpecialType.System_Int32, SpecialType.System_Int64,
        SpecialType.System_Decimal, SpecialType.System_Double,
        SpecialType.System_Single,
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
        var pcg = new PCG(stream, state);
        var seen = new HashSet<string>(rows.Select(r => string.Join(RowKeySep, r)));
        for (int k = 0; k < MaxSampleRows; k++)
        {
            var row = gens.Select(g => g.Generate(pcg, null, out _)).ToList();
            if (seen.Add(string.Join(RowKeySep, row))) rows.Add(row);
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
            case SpecialType.System_Single:
            {
                var (lo, hi) = DoubleRange(mined);
                return Gen.Double[lo, hi].Select(v => FmtFloat(Math.Round(v, 2)));
            }
            case SpecialType.System_Int32 or SpecialType.System_Int64:
            {
                var (lo, hi) = IntRange(mined);
                return Gen.Int[lo, hi].Select(v => v.ToString(CultureInfo.InvariantCulture));
            }
            case SpecialType.System_Boolean:
                return Gen.Bool.Select(v => v ? "true" : "false");
            default:
                return Gen.OneOfConst(candidates.ToArray());
        }
    }

    private static (uint Stream, ulong State) SeedFrom(string s)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return (BitConverter.ToUInt32(h, 0) | 1u, BitConverter.ToUInt64(h, 4));
    }

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
        double mx = Math.Min(mined.Doubles.Where(double.IsFinite).DefaultIfEmpty(0).Max(), 1_000_000);
        return (-10, Math.Max(10_000, mx * 2 + 100));
    }

    private const int StringCandidateCap = 10;

    /// <summary>
    /// Candidates for a top-level string parameter. Extracts the method's own guards on the parameter and
    /// emits a witness that clears them plus near-misses that fail one guard at a time
    /// (<see cref="StringGuardSolver"/>) — the conjunction cases the constant-miner can't reach. Falls back
    /// to the simple rich witness when the method applies no recognizable string guards.
    /// </summary>
    private List<string> StringCandidates(IParameterSymbol p, SyntaxNode? body, Mined mined)
    {
        var values = new List<string>();

        if (body is not null)
        {
            var guards = StringGuardSolver.Extract(body, p.Name);
            if (guards.Any)
                foreach (var s in StringGuardSolver.Candidates(guards))
                    values.Add(Quote(s));
        }

        if (values.Count == 0)
            values.Add(Quote(RichString(GuardLength(mined))));
        values.Add("\"12345\"");
        foreach (var s in mined.Strings) values.Add(Quote(s));
        values.Add("\"\""); values.Add("null");

        return values.Distinct().Take(StringCandidateCap).ToList();
    }

    /// <summary>Candidate source-literals for a parameter: mined constants + boundaries + a small catalog.</summary>
    private List<string> Candidates(ITypeSymbol type, Mined mined)
    {
        var t = Unwrap(type);
        var values = new List<string>();

        switch (t.SpecialType)
        {
            case SpecialType.System_String:
                values.Add(Quote(RichString(GuardLength(mined))));
                values.Add("\"12345\"");
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

            case SpecialType.System_Double:
                foreach (var v in mined.Doubles) AddNeighbours(values, v, FmtDouble);
                values.Add("0d"); values.Add("1d");
                break;

            case SpecialType.System_Single:
                foreach (var v in mined.Doubles) AddNeighbours(values, v, FmtFloat);
                values.Add("0f"); values.Add("1f");
                break;

            case SpecialType.System_Boolean:
                values.Add("false"); values.Add("true");
                break;

            case SpecialType.System_Char:
                values.Add("'a'"); values.Add("'0'");
                break;

            default:
                if (KnownFrameworkValue(t) is { } fw)
                {
                    values.Add(fw);
                }
                else if (t.TypeKind == TypeKind.Enum)
                {
                    values.AddRange(EnumMembers(t));
                }
                else if (ElementType(t) is { } elem)
                {
                    foreach (var e in ElementCandidates(elem, mined)) values.Add(CollectionLiteral(t, e));
                }
                else if (t is INamedTypeSymbol nt && AccessibleCtor(nt) is not null)
                {
                    values.AddRange(ConstructionVariants(nt, mined));
                }
                if (values.Count == 0) values.Add(BuildValue(t, 0));
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
        string fq = Fq(type);
        var ctor = AccessibleCtor(type);
        if (ctor is null) return new() { $"default({fq})!" };
        string init = RequiredInitializer(type, ctor, mined);
        if (ctor.Parameters.Length == 0) return new() { $"new {fq}(){init}" };

        var perArg = ctor.Parameters.Select(p => CtorArgCandidates(p.Type, mined)).ToList();
        var baseArgs = perArg.Select(c => c[0]).ToList();

        var variants = new List<string> { $"new {fq}({string.Join(", ", baseArgs)}){init}" };
        var seen = new HashSet<string> { string.Join("|", baseArgs) };
        for (int j = 1; variants.Count < MaxObjectVariants; j++)
        {
            bool progressed = false;
            for (int i = 0; i < perArg.Count && variants.Count < MaxObjectVariants; i++)
            {
                if (j >= perArg[i].Count) continue;
                progressed = true;
                var args = new List<string>(baseArgs) { [i] = perArg[i][j] };
                if (seen.Add(string.Join("|", args))) variants.Add($"new {fq}({string.Join(", ", args)}){init}");
            }
            if (!progressed) break;
        }
        return variants;
    }

    /// <summary>
    /// An object initializer covering every <c>required</c> member the constructor doesn't already set,
    /// e.g. <c>{ Name = "…", Count = 1 }</c>. Empty when the type has no required members or the chosen
    /// ctor is annotated <c>[SetsRequiredMembers]</c>. Without it, <c>new T(…)</c> on a type with required
    /// members is a CS9035 compile error and the whole generated test is dropped.
    /// </summary>
    private string RequiredInitializer(INamedTypeSymbol type, IMethodSymbol ctor, Mined mined)
    {
        if (ctor.GetAttributes().Any(a => a.AttributeClass?.Name == "SetsRequiredMembersAttribute"))
            return "";
        var required = RequiredMembers(type);
        if (required.Count == 0) return "";
        var sets = required.Select(m => $"{m.Name} = {CtorArgCandidates(MemberType(m), mined)[0]}");
        return " { " + string.Join(", ", sets) + " }";
    }

    /// <summary>The public <c>required</c> properties/fields of a type and its bases (each needs a value
    /// in an object initializer). De-duplicated by name so an overriding member isn't set twice.</summary>
    private static List<ISymbol> RequiredMembers(INamedTypeSymbol type)
    {
        var list = new List<ISymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            foreach (var m in t.GetMembers())
                if (m is (IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })
                    && m.DeclaredAccessibility == Accessibility.Public && seen.Add(m.Name))
                    list.Add(m);
        return list;
    }

    private static ITypeSymbol MemberType(ISymbol m) => m switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        _ => throw new InvalidOperationException("required member must be a property or field"),
    };

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
            case SpecialType.System_Double:
                v.Add("1d");
                foreach (var d in mined.Doubles) v.Add(FmtDouble(d));
                v.Add("0d");
                break;
            case SpecialType.System_Single:
                v.Add("1f");
                foreach (var d in mined.Doubles) v.Add(FmtFloat(d));
                v.Add("0f");
                break;
            case SpecialType.System_Boolean: v.Add("true"); v.Add("false"); break;
            case SpecialType.System_Char: v.Add("'a'"); v.Add("'0'"); break;
            default:
                if (KnownFrameworkValue(t) is { } fw) v.Add(fw);
                else if (t.TypeKind == TypeKind.Enum) v.AddRange(EnumMembers(t));
                else v.Add(BuildValue(t, 1));
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

    private static int GuardLength(Mined mined) =>
        Math.Min(64, mined.Ints.Where(i => i is >= 8 and <= 256).Select(i => (int)i).DefaultIfEmpty(20).Min());

    /// <summary>An alphanumeric string of the given length (letters + digits, never starts with '-').</summary>
    private static string RichString(int len)
    {
        var sb = new System.Text.StringBuilder(len);
        while (sb.Length < len) sb.Append("Ab1");
        return sb.ToString()[..len];
    }

    /// <summary>
    /// A real, substitutable default for common BCL abstractions, so a method that takes one is
    /// characterized with actual behaviour instead of a NullReferenceException. Curated and explainable
    /// — not a guess. The dominant real-world case is <c>IFormatProvider</c> (every culture-aware
    /// Format/ToString/Parse overload). Returns null for types not in the catalog (fall through to the
    /// normal construction logic).
    /// </summary>
    private static string? KnownFrameworkValue(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } g && g.TypeArguments.Length == 1)
        {
            string arg = Fq(g.TypeArguments[0]);
            switch (Fq(g.OriginalDefinition))
            {
                case "global::System.Collections.Generic.IComparer<T>":
                    return $"global::System.Collections.Generic.Comparer<{arg}>.Default";
                case "global::System.Collections.Generic.IEqualityComparer<T>":
                    return $"global::System.Collections.Generic.EqualityComparer<{arg}>.Default";
            }
        }

        return Fq(type) switch
        {
            "global::System.IFormatProvider" => "global::System.Globalization.CultureInfo.InvariantCulture",
            "global::System.Globalization.CultureInfo" => "global::System.Globalization.CultureInfo.InvariantCulture",
            "global::System.TimeZoneInfo" => "global::System.TimeZoneInfo.Utc",
            "global::System.Threading.CancellationToken" => "global::System.Threading.CancellationToken.None",
            "global::System.IO.TextWriter" => "global::System.IO.TextWriter.Null",
            "global::System.IO.TextReader" => "global::System.IO.TextReader.Null",
            "global::System.IO.Stream" => "global::System.IO.Stream.Null",
            "global::Ardalis.GuardClauses.IGuardClause" => "global::Ardalis.GuardClauses.Guard.Against",
            _ => null,
        };
    }

    private static List<string> EnumMembers(ITypeSymbol enumType) =>
        enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst)
            .Select(f => Fq(enumType) + "." + f.Name)
            .Take(MaxCandidatesPerParam).ToList();

    private static string FmtDecimal(decimal v) => v.ToString(CultureInfo.InvariantCulture) + "m";

    private static string FmtDouble(double v) =>
        double.IsNaN(v) ? "double.NaN"
        : double.IsPositiveInfinity(v) ? "double.PositiveInfinity"
        : double.IsNegativeInfinity(v) ? "double.NegativeInfinity"
        : v.ToString("R", CultureInfo.InvariantCulture) + "d";

    private static string FmtFloat(double v)
    {
        float f = (float)v;
        return float.IsNaN(f) ? "float.NaN"
            : float.IsPositiveInfinity(f) ? "float.PositiveInfinity"
            : float.IsNegativeInfinity(f) ? "float.NegativeInfinity"
            : f.ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    private string BuildSut(INamedTypeSymbol type)
    {
        string fq = Fq(type);
        var ctor = AccessibleCtor(type);
        if (ctor is not null)
        {
            var args = ctor.Parameters.Select(p => BuildValue(p.Type, 1));
            return $"new {fq}({string.Join(", ", args)}){RequiredInitializer(type, ctor, new Mined())}";
        }

        return StaticFactory(type) ?? $"default({fq})!";
    }

    /// <summary>
    /// A public static member on <paramref name="type"/> that yields an instance of it. Deterministic:
    /// candidates are sorted by name and the cleanest source is preferred — a parameterless property
    /// (e.g. <c>.Iso</c>, <c>.Utc</c>), then a static field singleton, then a parameterless factory
    /// method. Parameterless only, so the receiver expression itself can't throw on synthetic arguments
    /// (it runs outside the per-row try/catch).
    /// </summary>
    private static string? StaticFactory(INamedTypeSymbol type)
    {
        string fq = Fq(type);
        bool Yields(ITypeSymbol t) => SymbolEqualityComparer.Default.Equals(t, type);

        var prop = type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public
                        && p.GetMethod is { DeclaredAccessibility: Accessibility.Public } && Yields(p.Type))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (prop is not null) return $"{fq}.{prop.Name}";

        var field = type.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.IsStatic && f.DeclaredAccessibility == Accessibility.Public && Yields(f.Type))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (field is not null) return $"{fq}.{field.Name}";

        var method = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.IsStatic && m.MethodKind == MethodKind.Ordinary
                        && m.DeclaredAccessibility == Accessibility.Public
                        && !m.IsGenericMethod && m.Parameters.IsEmpty && Yields(m.ReturnType))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (method is not null) return $"{fq}.{method.Name}()";

        return null;
    }


    /// <summary>An interface implementable with trivial members and referenceable from the test
    /// assembly. Skips (→ falls back to default!) anything with generic methods, ref/out params,
    /// by-ref returns, or static-abstract members — keeping emitted stubs compilable.</summary>
    internal static bool CanStub(INamedTypeSymbol iface)
    {
        if (iface.TypeKind != TypeKind.Interface || !IsPubliclyAccessible(iface)) return false;
        if (iface.AllInterfaces.Any(i => i.TypeKind == TypeKind.Error)) return false;
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in StubMembers(iface))
        {
            switch (m)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, IsAbstract: true } method:
                    if (method.IsStatic || method.IsGenericMethod
                        || method.ReturnsByRef || method.ReturnsByRefReadonly) return false;
                    if (method.Parameters.Any(p => p.RefKind is RefKind.Ref or RefKind.Out or RefKind.RefReadOnly))
                        return false;
                    if (!signatures.Add(method.ToDisplayString())) return false;
                    break;
                case IPropertySymbol { IsAbstract: true } p:
                    if (p.IsStatic || p.ReturnsByRef || p.ReturnsByRefReadonly) return false;
                    if (!signatures.Add(p.ToDisplayString())) return false;
                    break;
                case IEventSymbol { IsAbstract: true, IsStatic: true }:
                    return false;
            }
        }
        return true;
    }

    private static bool IsPubliclyAccessible(INamedTypeSymbol t)
    {
        for (INamedTypeSymbol? cur = t; cur is not null; cur = cur.ContainingType)
            if (cur.DeclaredAccessibility != Accessibility.Public) return false;
        return true;
    }

    /// <summary>Every member that must be implemented — the interface's own abstract members plus those
    /// of every interface it inherits.</summary>
    private static IEnumerable<ISymbol> StubMembers(INamedTypeSymbol iface) =>
        iface.AllInterfaces.Append(iface).SelectMany(i => i.GetMembers());

    /// <summary>Register a stub class for the interface once and return `new __Stub()`.</summary>
    private string StubInstance(INamedTypeSymbol iface)
    {
        string fq = Fq(iface);
        if (!_stubs.TryGetValue(fq, out var s))
        {
            s = ($"__Stub_{SafeId(iface.Name)}_{ShortHash(fq)}", iface);
            _stubs[fq] = s;
        }
        return $"new {s.Name}()";
    }

    private void EmitStub(System.Text.StringBuilder sb, string name, INamedTypeSymbol iface)
    {
        sb.AppendLine($"    private sealed class {name} : {Fq(iface)}");
        sb.AppendLine("    {");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in StubMembers(iface))
        {
            if (!seen.Add(m.ToDisplayString())) continue;
            switch (m)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, IsAbstract: true } method:
                    string pars = string.Join(", ", method.Parameters.Select(StubParam));
                    if (method.ReturnsVoid)
                        sb.AppendLine($"        public void {method.Name}({pars}) {{ }}");
                    else
                        sb.AppendLine($"        public {Fq(method.ReturnType)} {method.Name}({pars}) => {ValueDefault(method.ReturnType)};");
                    break;
                case IPropertySymbol { IsAbstract: true } prop:
                    EmitStubProperty(sb, prop);
                    break;
                case IEventSymbol { IsAbstract: true } ev:
                    sb.AppendLine($"        public event {Fq(ev.Type)} {ev.Name} {{ add {{ }} remove {{ }} }}");
                    break;
            }
        }
        sb.AppendLine("    }");
    }

    private static string StubParam(IParameterSymbol p) =>
        (p.RefKind == RefKind.In ? "in " : "") + Fq(p.Type) + " " + p.Name;

    private void EmitStubProperty(System.Text.StringBuilder sb, IPropertySymbol prop)
    {
        string t = Fq(prop.Type);
        string getter = prop.GetMethod is not null ? $"get => {ValueDefault(prop.Type)}; " : "";
        string setter = prop.SetMethod is null ? "" : prop.SetMethod.IsInitOnly ? "init { } " : "set { } ";
        if (prop.IsIndexer)
            sb.AppendLine($"        public {t} this[{string.Join(", ", prop.Parameters.Select(StubParam))}] {{ {getter}{setter}}}");
        else
            sb.AppendLine($"        public {t} {prop.Name} {{ {getter}{setter}}}");
    }

    /// <summary>A trivial value expression for a stub member's return/getter — completed tasks, empty
    /// list-like collections, known framework values, else default!. Never recurses into another stub.</summary>
    private string ValueDefault(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n)
        {
            switch (Def(n.OriginalDefinition))
            {
                case "System.Threading.Tasks.Task": return "global::System.Threading.Tasks.Task.CompletedTask";
                case "System.Threading.Tasks.ValueTask": return "default";
                case "System.Threading.Tasks.Task<TResult>":
                    return $"global::System.Threading.Tasks.Task.FromResult<{Fq(n.TypeArguments[0])}>({ValueDefault(n.TypeArguments[0])})";
                case "System.Threading.Tasks.ValueTask<TResult>":
                    return $"new global::System.Threading.Tasks.ValueTask<{Fq(n.TypeArguments[0])}>({ValueDefault(n.TypeArguments[0])})";
            }
        }
        if (KnownFrameworkValue(t) is { } k) return k;
        if (t.TypeKind == TypeKind.Interface && IsListLike(t) && ElementType(t) is { } el)
            return $"global::System.Array.Empty<{Fq(el)}>()";
        return $"default({Fq(t)})!";
    }

    private static bool IsListLike(ITypeSymbol t) =>
        Def(t.OriginalDefinition) is
            "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IReadOnlyList<T>";

    private string BuildValue(ITypeSymbol type, int depth)
    {
        type = Unwrap(type);

        if (KnownFrameworkValue(type) is { } known) return known;

        var first = Unwrap(type).SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
                or SpecialType.System_Byte => "0",
            SpecialType.System_Decimal => "0m",
            SpecialType.System_Double => "0d",
            SpecialType.System_Single => "0f",
            SpecialType.System_Boolean => "true",
            SpecialType.System_String => "\"\"",
            SpecialType.System_Char => "'a'",
            _ => (string?)null,
        };
        if (first is not null) return first;

        if (type.TypeKind == TypeKind.Enum)
            return EnumMembers(type).FirstOrDefault() ?? $"default({Fq(type)})";

        if (depth <= 3)
        {
            var element = ElementType(type);
            if (element is not null)
                return CollectionLiteral(type, BuildValue(element, depth + 1));

            if (type is INamedTypeSymbol named && AccessibleCtor(named) is { } ctor)
            {
                var args = ctor.Parameters.Select(p => BuildValue(p.Type, depth + 1));
                return $"new {Fq(named)}({string.Join(", ", args)}){RequiredInitializer(named, ctor, new Mined())}";
            }
        }

        if (depth <= 2 && type is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface && CanStub(iface))
            return StubInstance(iface);

        return $"default({Fq(type)})!";
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

    /// <summary>
    /// A single-element collection literal whose static type satisfies <paramref name="type"/>. An array
    /// literal is assignable to arrays and the BCL collection INTERFACES (IEnumerable&lt;T&gt;, IList&lt;T&gt;,
    /// IReadOnlyList&lt;T&gt;, …) but NOT to a concrete <c>List&lt;T&gt;</c>/<c>HashSet&lt;T&gt;</c> — so for a
    /// concrete collection use a collection initializer on the real type (or an empty instance when it has
    /// no single-arg <c>Add</c>, e.g. Queue/Stack). Without this, a <c>List&lt;int&gt;</c> parameter gets an
    /// <c>int[]</c> and the test fails to compile (found dogfooding nopCommerce).
    /// </summary>
    private static string CollectionLiteral(ITypeSymbol type, string element)
    {
        if (type is IArrayTypeSymbol || IsArrayCompatibleInterface(type))
            return $"new[] {{ {element} }}";

        if (type is INamedTypeSymbol named
            && named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            string fq = Fq(named);
            bool hasAdd = named.GetMembers("Add").OfType<IMethodSymbol>()
                .Any(m => m.Parameters.Length == 1 && m.DeclaredAccessibility == Accessibility.Public);
            return hasAdd ? $"new {fq} {{ {element} }}" : $"new {fq}()";
        }

        return $"new[] {{ {element} }}";
    }

    private static bool IsArrayCompatibleInterface(ITypeSymbol t) =>
        t.TypeKind == TypeKind.Interface && Def(t.OriginalDefinition) is
            "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IReadOnlyList<T>"
            or "System.Collections.IEnumerable" or "System.Collections.ICollection" or "System.Collections.IList";

    private static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is IArrayTypeSymbol arr) return arr.ElementType;

        if (type is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1
            && Def(n.OriginalDefinition).StartsWith("System.Collections.Generic.", StringComparison.Ordinal))
            return n.TypeArguments[0];

        var ienum = type.AllInterfaces.FirstOrDefault(i => i.Name == "IEnumerable" && i.TypeArguments.Length == 1);
        return ienum?.TypeArguments.FirstOrDefault();
    }

    private static string SafeId(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\W", "_");

    /// <summary>A fully-escaped C# string literal (with quotes) for <paramref name="s"/>. Uses Roslyn's
    /// formatter so newlines, tabs, NUL, and Unicode line separators (U+0085/2028/2029) — all illegal raw
    /// in a regular string literal — are escaped, not emitted verbatim (which would be CS1010).</summary>
    private static string Quote(string s) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(s, quote: true);

    /// <summary>Escapes a string for embedding inside an existing <c>"…"</c> literal (the Input/description
    /// text). Display tokens are already valid C# literals via <see cref="Quote"/>, but harden against
    /// backslash, quote, and raw control chars so the description can never break the emitted file.</summary>
    private static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
