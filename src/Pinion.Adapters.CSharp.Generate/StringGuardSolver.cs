using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>
/// Synthesizes string inputs that exercise the CONJUNCTION of guards a method applies to a string
/// parameter — the case the constant-miner alone can't crack (a token that must be 16+ chars AND contain
/// a letter AND a digit AND not start with '-'). It does not solve for "the passing input" (which needs
/// control-flow polarity that's hard in general); instead it varies each guard ACROSS its boundary, so
/// inputs land on both sides of every decision — which is exactly what kills the mutants the simple
/// generator leaves alive. Constraint synthesis by construction: BCL-only, deterministic, no SMT.
/// </summary>
internal static class StringGuardSolver
{
    // Don't emit absurd literals: cap synthesized lengths so a `Length > 100000` guard can't produce a
    // 100k-char string in the generated test.
    private const int MaxSynthLength = 512;

    /// <summary>The string guards extracted for one parameter. <see cref="Any"/> is false when none apply.</summary>
    internal sealed record StringGuards(
        IReadOnlyList<int> Lengths,
        string? Prefix,
        string? Suffix,
        IReadOnlyList<string> Contains,
        IReadOnlyList<string> Exact,
        bool RequireLetter,
        bool RequireDigit,
        IReadOnlyList<string> Regexes)
    {
        // Engage only for STRUCTURAL guards (length / char-class / affix / regex) — the conjunctions the
        // constant-miner can't satisfy. Pure `== "literal"` equality is already covered by mined strings,
        // so Exact alone does not trigger the solver (it's still included when it does engage).
        public bool Any => Lengths.Count > 0 || Prefix is not null || Suffix is not null
            || Contains.Count > 0 || RequireLetter || RequireDigit || Regexes.Count > 0;
    }

    /// <summary>Extract the guards method <paramref name="body"/> applies to the parameter named
    /// <paramref name="paramName"/>. Purely syntactic — receiver matched by identifier name.</summary>
    public static StringGuards Extract(SyntaxNode body, string paramName)
    {
        var lengths = new SortedSet<int>();
        string? prefix = null, suffix = null;
        var contains = new List<string>();
        var exact = new List<string>();
        var regexes = new List<string>();
        bool requireLetter = false, requireDigit = false;

        bool IsParam(ExpressionSyntax e) =>
            e is IdentifierNameSyntax id && id.Identifier.ValueText == paramName;

        static bool IsRegexType(string typeText) =>
            typeText is "Regex" or "System.Text.RegularExpressions.Regex";

        // `p.Length` member access.
        bool IsParamLength(ExpressionSyntax e) =>
            e is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" } m && IsParam(m.Expression);

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                // p.Length <op> N  (or N <op> p.Length) — collect the length boundary constants.
                case BinaryExpressionSyntax be when IsComparison(be.Kind()):
                    if (IsParamLength(be.Left) && IntLiteral(be.Right) is { } n1) lengths.Add(n1);
                    else if (IsParamLength(be.Right) && IntLiteral(be.Left) is { } n2) lengths.Add(n2);
                    // p == "literal" / "literal" == p
                    else if (be.IsKind(SyntaxKind.EqualsExpression))
                    {
                        if (IsParam(be.Left) && StringLiteral(be.Right) is { } r) exact.Add(r);
                        else if (IsParam(be.Right) && StringLiteral(be.Left) is { } l) exact.Add(l);
                    }
                    break;

                case InvocationExpressionSyntax inv when inv.Expression is MemberAccessExpressionSyntax ma:
                    string member = ma.Name.Identifier.ValueText;

                    // p.StartsWith("x") / p.EndsWith("x") / p.Contains("x") / p.Equals("x")
                    if (IsParam(ma.Expression) && inv.ArgumentList.Arguments.Count >= 1
                        && StringLiteral(inv.ArgumentList.Arguments[0].Expression) is { } lit && lit.Length > 0)
                    {
                        switch (member)
                        {
                            case "StartsWith": prefix ??= lit; break;
                            case "EndsWith": suffix ??= lit; break;
                            case "Contains": contains.Add(lit); break;
                            case "Equals": exact.Add(lit); break;
                        }
                    }

                    // char.IsLetter(...) / char.IsDigit(...) anywhere → the input is expected to vary on those.
                    // The receiver `char` is a predefined-type keyword (not an identifier), so match by text;
                    // also accept the qualified `Char`/`System.Char` forms.
                    if (ma.Expression.ToString() is "char" or "Char" or "System.Char" or "global::System.Char")
                    {
                        if (member is "IsLetter" or "IsLetterOrDigit") requireLetter = true;
                        if (member is "IsDigit" or "IsLetterOrDigit") requireDigit = true;
                    }

                    // Regex.IsMatch(p, "pattern" [, options])  OR  new Regex("pattern").IsMatch(p)
                    if (member == "IsMatch")
                    {
                        var a = inv.ArgumentList.Arguments;
                        if (IsRegexType(ma.Expression.ToString()) && a.Count >= 2 && IsParam(a[0].Expression)
                            && StringLiteral(a[1].Expression) is { } staticPat)
                            regexes.Add(staticPat);
                        else if (ma.Expression is ObjectCreationExpressionSyntax oce && IsRegexType(oce.Type.ToString())
                            && oce.ArgumentList is { Arguments.Count: >= 1 } cargs
                            && StringLiteral(cargs.Arguments[0].Expression) is { } instancePat
                            && a.Count >= 1 && IsParam(a[0].Expression))
                            regexes.Add(instancePat);
                    }
                    break;
            }
        }

        return new StringGuards(
            lengths.ToList(), prefix, suffix,
            contains.Distinct(StringComparer.Ordinal).ToList(),
            exact.Distinct(StringComparer.Ordinal).ToList(),
            requireLetter, requireDigit,
            regexes.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Ordered, deduplicated string VALUES (unquoted) that span each guard's boundary — base witness first,
    /// then the highest-value near-misses. Empty/null are added by the caller. Deterministic.
    /// </summary>
    public static IReadOnlyList<string> Candidates(StringGuards g)
    {
        int floor = g.Lengths.Count > 0 ? Math.Min(MaxSynthLength, g.Lengths.Min()) : 20;
        int baseLen = Math.Max(1, floor);

        var values = new List<string>();
        void Add(string s) { if (s.Length <= MaxSynthLength) values.Add(s); }

        // 1. Base witness — alnum (letter+digit), long enough to clear the smallest length floor.
        Add(Alnum(baseLen));

        // 2. Just under the smallest length floor — exercises the minimum-length guard.
        if (floor >= 2) Add(Alnum(floor - 1));

        // 3/4. Char-class near-misses — only digits (no letter), only letters (no digit).
        if (g.RequireLetter || g.RequireDigit)
        {
            Add(Digits(baseLen));  // no letter
            Add(Letters(baseLen)); // no digit
        }

        // 5/6/7. Affix variants — cover the "has this affix" side regardless of guard polarity.
        if (g.Prefix is { } p) Add(p + Alnum(baseLen));
        if (g.Contains.Count > 0) Add(Alnum(baseLen) + g.Contains[0]);
        if (g.Suffix is { } s) Add(Alnum(baseLen) + s);

        // 8. Just over an upper length bound (when it's a sane size).
        if (g.Lengths.Count > 0)
        {
            int hi = g.Lengths.Max();
            if (hi > floor && hi < MaxSynthLength) Add(Alnum(hi + 1));
        }

        // 9. Regex guards: a string that MATCHES the pattern (reaches the accept branch the simple witness
        //    never hits) plus a verified non-match. RegexSampler only returns a .NET-verified match, so a
        //    pattern it can't model is simply skipped.
        foreach (var pat in g.Regexes)
        {
            if (RegexSampler.GenerateMatch(pat) is not { } match) continue;
            Add(match);
            if (RegexSampler.GenerateNonMatch(pat, match) is { } nonMatch) Add(nonMatch);
        }

        // 10. Exact-equality literals — trivially exercise the `== "literal"` branch.
        foreach (var e in g.Exact) Add(e);

        return values.Distinct(StringComparer.Ordinal).ToList();
    }

    // Alphanumeric, matches the synthesizer's RichString so the no-affix base stays identical.
    private static string Alnum(int len) => Repeat("Ab1", len);
    private static string Letters(int len) => Repeat("Abc", len);
    private static string Digits(int len) => Repeat("123", len);

    private static string Repeat(string unit, int len)
    {
        var sb = new System.Text.StringBuilder(len);
        while (sb.Length < len) sb.Append(unit);
        return sb.ToString()[..len];
    }

    private static int? IntLiteral(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax { Token.Value: int i } ? i : null;

    private static string? StringLiteral(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText : null;

    private static bool IsComparison(SyntaxKind kind) => kind is
        SyntaxKind.LessThanExpression or SyntaxKind.GreaterThanExpression
        or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanOrEqualExpression
        or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;
}
