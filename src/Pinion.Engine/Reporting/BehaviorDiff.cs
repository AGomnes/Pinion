using System.Text;

namespace Pinion.Engine.Reporting;

/// <summary>
/// Minimal line-level diff (LCS) rendered as a unified-style hunk — enough to SHOW exactly how a
/// method's recorded behavior changed after a migration. By convention <c>-</c> lines are the locked
/// golden master (what the behavior WAS) and <c>+</c> lines are the current code's output (what it
/// is NOW). Language-agnostic: it diffs two snapshot texts, nothing more.
/// </summary>
public static class BehaviorDiff
{
    private enum Op { Equal, Del, Ins }
    private readonly record struct Edit(Op Op, string Text);

    /// <summary>A unified-style diff of <paramref name="locked"/> (−) vs <paramref name="current"/> (+),
    /// keeping <paramref name="context"/> unchanged lines around each change. Empty string if identical.</summary>
    public static string Unified(string locked, string current, int context = 3)
    {
        var a = SplitLines(locked);
        var b = SplitLines(current);
        return Render(Diff(a, b), context);
    }

    private static string[] SplitLines(string s) => s.Replace("\r\n", "\n").Split('\n');

    private static List<Edit> Diff(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var edits = new List<Edit>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { edits.Add(new(Op.Equal, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { edits.Add(new(Op.Del, a[x])); x++; }
            else { edits.Add(new(Op.Ins, b[y])); y++; }
        }
        while (x < n) edits.Add(new(Op.Del, a[x++]));
        while (y < m) edits.Add(new(Op.Ins, b[y++]));
        return edits;
    }

    private static string Render(List<Edit> edits, int context)
    {
        if (!edits.Any(e => e.Op != Op.Equal)) return "";

        var keep = new bool[edits.Count];
        for (int i = 0; i < edits.Count; i++)
            if (edits[i].Op != Op.Equal)
                for (int k = Math.Max(0, i - context); k <= Math.Min(edits.Count - 1, i + context); k++)
                    keep[k] = true;

        var sb = new StringBuilder();
        bool gap = false;
        for (int i = 0; i < edits.Count; i++)
        {
            if (!keep[i]) { gap = true; continue; }
            if (gap) { sb.AppendLine("  …"); gap = false; }
            var e = edits[i];
            sb.AppendLine((e.Op switch { Op.Del => "- ", Op.Ins => "+ ", _ => "  " }) + e.Text);
        }
        return sb.ToString().TrimEnd('\n');
    }
}
