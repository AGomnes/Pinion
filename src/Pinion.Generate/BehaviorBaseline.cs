using Pinion.Engine.Reporting;

namespace Pinion.Generate;

/// <summary>
/// Re-baselining: accept the current output of a changed behavior as its new golden master. This is the
/// triage action after <c>verify</c> shows changes — the user reviews each diff, accepts the INTENDED
/// ones (so the suite tracks the new expected behavior), and what's left is a real regression to fix.
/// </summary>
public static class BehaviorBaseline
{
    /// <summary>Promote each entry's current output (<c>*.received.*</c>) to its golden master
    /// (<c>*.verified.*</c>), scrubbing secrets/PII first (the master gets committed). Returns the number
    /// re-baselined. Entries without a received file (i.e. not actually changed) are skipped.</summary>
    public static int Accept(IEnumerable<BehaviorDiffEntry> entries)
    {
        int accepted = 0;
        foreach (var e in entries)
        {
            if (e.ReceivedPath is null || !File.Exists(e.ReceivedPath)) continue;
            string scrubbed = SecretScrubber.Scrub(File.ReadAllText(e.ReceivedPath)).Text;
            File.WriteAllText(e.VerifiedPath, scrubbed);
            File.Delete(e.ReceivedPath);
            accepted++;
        }
        return accepted;
    }
}
