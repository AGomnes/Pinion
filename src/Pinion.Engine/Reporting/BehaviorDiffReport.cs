namespace Pinion.Engine.Reporting;

/// <summary>Whether a locked method's behavior survived a migration unchanged.</summary>
public enum BehaviorChange
{
    /// <summary>Current output matches the golden master — behavior preserved.</summary>
    Identical,

    /// <summary>Current output differs from the golden master — behavior changed.</summary>
    Changed,
}

/// <summary>One locked method's verdict after re-running its characterization test against the
/// (now migrated) code.</summary>
public sealed record BehaviorDiffEntry(
    string Name,
    BehaviorChange Status,
    string? Diff,            // unified diff (− was / + now) when Changed; null when Identical
    string VerifiedPath,
    string? ReceivedPath);

/// <summary>
/// The behavior-diff report — Pinion's proof artifact. After you migrate, this answers the only
/// question that matters: which locked methods still behave identically, and exactly how the rest
/// changed. Pure data; renderers turn it into console / Markdown / JSON.
/// </summary>
public sealed record BehaviorDiffReport(
    string TestProject,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<BehaviorDiffEntry> Entries,
    bool BuildFailed = false,
    IReadOnlyList<string>? BuildErrors = null)
{
    public int Total => Entries.Count;
    public int Changed => Entries.Count(e => e.Status == BehaviorChange.Changed);
    public int Identical => Total - Changed;

    /// <summary>True when every locked method behaves identically and the suite still compiled.</summary>
    public bool BehaviorPreserved => !BuildFailed && Changed == 0 && Total > 0;
}
