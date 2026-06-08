using System.Text.RegularExpressions;
using Pinion.Engine.Model;

namespace Pinion.Generate;

/// <summary>
/// Safety filters for which methods `generate` will run. Generating a test EXECUTES the target
/// method, so a method that touches the filesystem, a database, the network, or money could cause
/// real side effects (delete data, send email, charge a card) when characterized. These guards
/// keep such methods out of a run unless explicitly allowed, and let the user exclude anything.
/// </summary>
public static class TargetGuards
{
    /// <summary>True if the method is tagged as potentially side-effecting (io) or money-touching.</summary>
    public static bool IsSideEffecting(CodeUnit unit) =>
        unit.DomainTags.Contains(DomainTag.Io) || unit.DomainTags.Contains(DomainTag.Money);

    /// <summary>True if any exclusion pattern matches the unit's file path, display name, or id.
    /// An excluded method is dropped from the run entirely — never run, never sent.</summary>
    public static bool IsExcluded(CodeUnit unit, IReadOnlyList<string> patterns) => MatchesAny(unit, patterns);

    /// <summary>
    /// True if any never-send pattern matches the unit's file path, namespace-qualified id, or
    /// display name. A matched unit's source must NEVER leave the machine: the AI path is refused
    /// for it (it can still be characterized offline with the deterministic provider). This is the
    /// security control from the spec — distinct from <see cref="IsExcluded"/>, which removes the
    /// method from the run; a never-send method is still analyzed and locally characterizable, it
    /// just may not be sent to a third-party model. Patterns are file globs or namespace prefixes
    /// (the id is namespace-qualified, e.g. "MyCompany.Secrets.Vault.Decrypt(string)").
    /// </summary>
    public static bool IsNeverSend(CodeUnit unit, IReadOnlyList<string> patterns) => MatchesAny(unit, patterns);

    private static bool MatchesAny(CodeUnit unit, IReadOnlyList<string> patterns)
    {
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (Matches(unit.FilePath, p) || Matches(unit.DisplayName, p) || Matches(unit.Id, p))
                return true;
        }
        return false;
    }

    private static bool Matches(string value, string pattern)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string norm = value.Replace('\\', '/');
        string pat = pattern.Trim().Replace('\\', '/');

        if (pat.Contains('*') || pat.Contains('?'))
        {
            string rx = "^.*" + Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + ".*$";
            return Regex.IsMatch(norm, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return norm.Contains(pat, StringComparison.OrdinalIgnoreCase);
    }
}
