using System.Text;
using System.Text.RegularExpressions;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>
/// Generates ONE string that matches a regex pattern — so a method guarded by <c>Regex.IsMatch(p, …)</c>
/// gets an input that reaches the ACCEPT branch, not just the reject path a random "Ab1…" lands on.
/// A pragmatic recursive-descent sampler over the common regex constructs (literals, char classes,
/// quantifiers, groups, alternation, the usual escapes); it bails to null on anything it doesn't model
/// (lookarounds, backreferences, …). Every result is VERIFIED with <see cref="Regex"/> before return, so
/// the contract is "null or a genuine match" — a sampler bug can only under-cover, never emit a wrong input.
/// </summary>
internal static class RegexSampler
{
    private const int MaxRepeat = 64;     // cap a `{n}` / `+` expansion so a huge count can't blow up the literal
    private const int MaxLength = 512;

    public static string? GenerateMatch(string pattern)
    {
        try
        {
            _ = new Regex(pattern); // reject patterns that don't even compile
            int pos = 0;
            string? sample = ParseAlternation(pattern, ref pos);
            if (sample is null || pos != pattern.Length || sample.Length > MaxLength) return null;
            return Regex.IsMatch(sample, pattern) ? sample : null; // the safety net
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A string that does NOT match — corrupt a known match until <see cref="Regex"/> agrees.</summary>
    public static string? GenerateNonMatch(string pattern, string match)
    {
        foreach (var candidate in new[] { match + "!", "!" + match, match.Length > 0 ? match[..^1] : "x", "z9z9z9" + match })
            if (candidate.Length <= MaxLength && !SafeIsMatch(pattern, candidate))
                return candidate;
        return null;
    }

    private static bool SafeIsMatch(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern); } catch { return true; } // treat errors as "matches" → skip it
    }

    // alternation : sequence ('|' sequence)*  — we take the first branch but must parse the rest to advance.
    private static string? ParseAlternation(string p, ref int i)
    {
        string? first = ParseSequence(p, ref i);
        if (first is null) return null;
        while (i < p.Length && p[i] == '|')
        {
            i++;
            if (ParseSequence(p, ref i) is null) return null; // parse-and-discard
        }
        return first;
    }

    // sequence : (atom quantifier?)*  — stops at '|' or ')' or end.
    private static string? ParseSequence(string p, ref int i)
    {
        var sb = new StringBuilder();
        while (i < p.Length && p[i] != '|' && p[i] != ')')
        {
            int start = i;
            string? atom = ParseAtom(p, ref i);
            if (atom is null) return null;
            sb.Append(ApplyQuantifier(p, ref i, atom));
            if (i == start) return null; // no progress → give up rather than loop
            if (sb.Length > MaxLength) return null;
        }
        return sb.ToString();
    }

    private static string? ParseAtom(string p, ref int i)
    {
        char c = p[i];
        switch (c)
        {
            case '^':
            case '$':
                i++;
                return ""; // anchors are zero-width

            case '(':
            {
                i++;
                if (i < p.Length && p[i] == '?')
                {
                    // (?:…) non-capturing is fine; (?<name>…) named is fine; lookarounds/conditionals/flags are not.
                    if (i + 1 < p.Length && p[i + 1] == ':') i += 2;
                    else if (i + 1 < p.Length && p[i + 1] == '<' && i + 2 < p.Length && p[i + 2] is not ('=' or '!'))
                    {
                        int gt = p.IndexOf('>', i);
                        if (gt < 0) return null;
                        i = gt + 1;
                    }
                    else return null;
                }
                string? inner = ParseAlternation(p, ref i);
                if (inner is null || i >= p.Length || p[i] != ')') return null;
                i++; // consume ')'
                return inner;
            }

            case '[':
                return ParseCharClass(p, ref i);

            case '\\':
            {
                char? e = ParseEscapeChar(p, ref i);
                return e?.ToString();
            }

            case '.':
                i++;
                return "a";

            default:
                i++;
                return c.ToString();
        }
    }

    private static string ApplyQuantifier(string p, ref int i, string atom)
    {
        if (i >= p.Length) return atom;
        int count;
        switch (p[i])
        {
            case '*': i++; count = 1; break; // 0+ → emit one for a meaningful sample
            case '+': i++; count = 1; break; // 1+
            case '?': i++; count = 1; break; // 0/1 → include it
            case '{':
            {
                int close = p.IndexOf('}', i);
                if (close < 0) return atom;
                string spec = p.Substring(i + 1, close - i - 1);
                i = close + 1;
                count = int.TryParse(spec.Split(',')[0], out int n) ? n : 1;
                break;
            }
            default:
                return atom;
        }
        if (i < p.Length && p[i] == '?') i++; // lazy marker after a quantifier (+?, *?, {n,m}?)
        count = Math.Clamp(count, 0, MaxRepeat);
        return count == 1 ? atom : string.Concat(Enumerable.Repeat(atom, count));
    }

    private static string? ParseCharClass(string p, ref int i)
    {
        i++; // consume '['
        bool negated = i < p.Length && p[i] == '^';
        if (negated) i++;

        var chars = new List<char>();
        var ranges = new List<(char Lo, char Hi)>();

        while (i < p.Length && p[i] != ']')
        {
            char lo;
            if (p[i] == '\\')
            {
                char? e = ParseEscapeChar(p, ref i);
                if (e is null) return null;
                lo = e.Value;
            }
            else { lo = p[i]; i++; }

            if (i + 1 < p.Length && p[i] == '-' && p[i + 1] != ']')
            {
                i++; // consume '-'
                char hi;
                if (p[i] == '\\') { char? e = ParseEscapeChar(p, ref i); if (e is null) return null; hi = e.Value; }
                else { hi = p[i]; i++; }
                ranges.Add((lo, hi));
            }
            else chars.Add(lo);
        }
        if (i >= p.Length) return null; // unterminated
        i++; // consume ']'

        if (!negated)
        {
            if (chars.Count > 0) return chars[0].ToString();
            if (ranges.Count > 0) return ranges[0].Lo.ToString();
            return null;
        }

        // Negated: find a printable char that's in none of the listed members.
        foreach (char cand in "Aa0 _-.xyz1") // a small, varied catalog
            if (!chars.Contains(cand) && !ranges.Any(r => cand >= r.Lo && cand <= r.Hi))
                return cand.ToString();
        return null;
    }

    // A representative single character for an escape, or null for zero-width / unsupported escapes.
    private static char? ParseEscapeChar(string p, ref int i)
    {
        i++; // consume '\'
        if (i >= p.Length) return null;
        char e = p[i];
        i++;
        return e switch
        {
            'd' => '5',
            'w' => 'a',
            's' => ' ',
            'D' => 'x',
            'W' => '-',
            'S' => 'x',
            't' => '\t',
            'n' => '\n',
            'r' => '\r',
            'b' or 'B' or 'A' or 'Z' or 'z' or 'G' => null, // word/string boundaries — zero-width, bail
            _ => e, // an escaped literal: \. \\ \+ \* \( \) \[ \] \{ \} \^ \$ \| \/ \- …
        };
    }
}
