using System.Text.RegularExpressions;
using Pinion.Engine.Reporting;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Runs the locked characterization suite against the CURRENT (e.g. post-migration) code and reports
/// which methods still behave identically and exactly how the rest changed. The proof step of the
/// "change everything, break nothing, prove it" loop.
///
/// Mechanism: Verify fails a characterization test when current output differs from the committed
/// <c>*.verified.*</c> golden master, writing a <c>*.received.*</c> with the new output. So after one
/// <c>dotnet test</c>: a master with a sibling <c>*.received.*</c> = behaviour changed (diff = verified
/// vs received); a master without one = identical.
/// </summary>
public sealed class BehaviorVerifier
{
    private readonly Action<string>? _log;
    public BehaviorVerifier(Action<string>? log = null) => _log = log;

    /// <summary>Hard timeout for the whole test run.</summary>
    public int RunTimeoutSeconds { get; set; } = 600;

    public async Task<BehaviorDiffReport> VerifyAsync(
        string testProjectPath, string generatedSubdir = "PinionCharacterization", CancellationToken ct = default,
        IReadOnlyCollection<string>? onlyTestClasses = null)
    {
        string full = Path.GetFullPath(testProjectPath);
        string outDir = Path.Combine(Path.GetDirectoryName(full)!, generatedSubdir);

        // Clear any stale received files so we only interpret THIS run's mismatches.
        if (Directory.Exists(outDir))
            foreach (var stale in Directory.GetFiles(outDir, "*.received.*")) { try { File.Delete(stale); } catch { } }

        // Scope (verify --since): run only the characterization classes covering changed code. An empty set
        // means "scope to nothing" — the caller handles that before calling us, so here null/empty = run all.
        bool scoped = onlyTestClasses is { Count: > 0 };
        string filter = "FullyQualifiedName~Pinion.Generated";
        if (scoped)
            filter += " & (" + string.Join("|", onlyTestClasses!.Select(c => "FullyQualifiedName~" + c)) + ")";

        _log?.Invoke($"[verify] running {(scoped ? $"{onlyTestClasses!.Count} affected " : "")}locked characterization test(s) in {Path.GetFileName(full)} …");
        var env = new Dictionary<string, string> { ["DiffEngine_Disabled"] = "true" };
        string[] args = { "test", full, "--nologo", "--verbosity", "quiet", "--filter", filter };
        var run = await ProcessRunner.RunAsync(
            "dotnet", args, env: env, timeout: TimeSpan.FromSeconds(Math.Max(60, RunTimeoutSeconds)), ct: ct).ConfigureAwait(false);

        if (HasBuildError(run.Combined))
            return new BehaviorDiffReport(full, DateTimeOffset.Now, Array.Empty<BehaviorDiffEntry>(),
                BuildFailed: true, BuildErrors: ExtractCompilerErrors(run.Combined));

        var classes = scoped ? new HashSet<string>(onlyTestClasses!, StringComparer.Ordinal) : null;
        var entries = new List<BehaviorDiffEntry>();
        if (Directory.Exists(outDir))
        {
            foreach (var verified in Directory.GetFiles(outDir, "*.verified.*").OrderBy(p => p, StringComparer.Ordinal))
            {
                // When scoped, only count snapshots whose class is in the affected set (filename is
                // "<ClassName>.<method>_characterization.verified.<ext>").
                if (classes is not null && !classes.Contains(ClassNameOf(verified))) continue;
                string received = verified.Replace(".verified.", ".received.");
                string name = SnapshotName(verified);
                if (File.Exists(received))
                {
                    string diff = BehaviorDiff.Unified(File.ReadAllText(verified), File.ReadAllText(received));
                    entries.Add(new BehaviorDiffEntry(name, BehaviorChange.Changed, diff, verified, received));
                }
                else
                {
                    entries.Add(new BehaviorDiffEntry(name, BehaviorChange.Identical, null, verified, null));
                }
            }
        }

        _log?.Invoke($"[verify] {entries.Count(e => e.Status == BehaviorChange.Changed)} changed / {entries.Count} total.");
        return new BehaviorDiffReport(full, DateTimeOffset.Now, entries);
    }

    /// <summary>"LocalDatePattern_Format_ec5e65_CharacterizationTests.Format_characterization.verified.txt"
    /// → "LocalDatePattern.Format" (best-effort: strip the test-class scaffolding and overload hash).</summary>
    private static string SnapshotName(string verifiedPath)
    {
        string className = ClassNameOf(verifiedPath);
        className = Regex.Replace(className, "_CharacterizationTests$", "");
        className = Regex.Replace(className, "_[0-9a-f]{6}$", "");   // overload disambiguation hash
        return className.Replace('_', '.');
    }

    /// <summary>The generated test class name from a snapshot path — the filename up to the first '.'
    /// ("AuthHandler_ValidateToken_1bf177_CharacterizationTests.ValidateToken_…verified.txt" → the class).</summary>
    private static string ClassNameOf(string verifiedPath)
    {
        string file = Path.GetFileName(verifiedPath);
        int dot = file.IndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static bool HasBuildError(string output) =>
        output.Contains("error CS", StringComparison.Ordinal) || output.Contains("Build FAILED", StringComparison.Ordinal);

    private static IReadOnlyList<string> ExtractCompilerErrors(string output) =>
        output.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains("error CS", StringComparison.Ordinal))
            .Distinct().Take(25).ToList();
}
