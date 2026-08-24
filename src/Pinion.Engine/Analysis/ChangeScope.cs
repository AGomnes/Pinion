using Pinion.Engine.Model;

namespace Pinion.Engine.Analysis;

/// <summary>
/// Scopes a set of code units to "what a source change could affect" — the methods declared in the
/// changed files, plus everything that (transitively) calls them. This is the blast radius behind
/// <c>verify --since</c>: re-verify only the locked behaviors a change can move, not the whole suite.
/// </summary>
public static class ChangeScope
{
    /// <summary>Units in <paramref name="units"/> whose source file is in <paramref name="changedFullPaths"/>,
    /// plus their transitive callers (via <see cref="CodeUnit.CallerIds"/>). Path comparison is by full
    /// path, case-insensitive (Windows-friendly).</summary>
    public static IReadOnlyList<CodeUnit> Affected(IReadOnlyList<CodeUnit> units, IEnumerable<string> changedFullPaths)
    {
        var changed = new HashSet<string>(
            changedFullPaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        if (changed.Count == 0) return Array.Empty<CodeUnit>();

        var byId = new Dictionary<string, CodeUnit>(StringComparer.Ordinal);
        foreach (var u in units) byId[u.Id] = u;

        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<CodeUnit>();
        foreach (var u in units)
            if (changed.Contains(NormalizePath(u.FilePath)) && affected.Add(u.Id))
                queue.Enqueue(u);

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var callerId in u.CallerIds)
                if (affected.Add(callerId) && byId.TryGetValue(callerId, out var caller))
                    queue.Enqueue(caller);
        }

        return units.Where(u => affected.Contains(u.Id)).ToList();
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}
