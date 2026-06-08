namespace LegacyShop;

/// <summary>
/// Two contrasting shapes for Feathers seam analysis (what `analyze` reports under "Seams to introduce").
/// </summary>

/// <summary>
/// Seam-HOSTILE: hard-wires the clock and the file system, so it can't be characterized as-is. The
/// report flags it "needs seam: DateTime.Now, File" — introduce a seam (Extract Interface /
/// Parameterize Constructor) before locking its behavior.
/// </summary>
public sealed class LegacyAuditLog
{
    public void Record(string action)
    {
        var line = System.DateTime.Now.ToString("o") + " " + action;
        System.IO.File.AppendAllText("audit.log", line + System.Environment.NewLine);
    }
}

/// <summary>The collaborator the seam-friendly version below depends on (a substitutable abstraction).</summary>
public interface IClock
{
    System.DateTime Now { get; }
}

/// <summary>
/// Seam-FRIENDLY: the same behavior, but the clock is injected. The report shows a seam is already
/// available (IClock), so this can be put under test today by passing a fake clock.
/// </summary>
public sealed class AuditLog
{
    private readonly IClock _clock;
    public AuditLog(IClock clock) => _clock = clock;

    public string Format(string action) => _clock.Now.ToString("o") + " " + action;
}
