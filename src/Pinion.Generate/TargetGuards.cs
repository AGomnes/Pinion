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

    /// <summary>True if any exclusion pattern matches the unit's file path, display name, or id.</summary>
    public static bool IsExcluded(CodeUnit unit, IReadOnlyList<string> patterns)
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
