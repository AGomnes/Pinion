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
