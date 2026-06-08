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
        string testProjectPath, string generatedSubdir = "PinionCharacterization", CancellationToken ct = default)
    {
        string full = Path.GetFullPath(testProjectPath);
        string outDir = Path.Combine(Path.GetDirectoryName(full)!, generatedSubdir);

        // Clear any stale received files so we only interpret THIS run's mismatches.
        if (Directory.Exists(outDir))
            foreach (var stale in Directory.GetFiles(outDir, "*.received.*")) { try { File.Delete(stale); } catch { } }

        _log?.Invoke($"[verify] running locked characterization tests in {Path.GetFileName(full)} …");
        var env = new Dictionary<string, string> { ["DiffEngine_Disabled"] = "true" };
        string[] args = { "test", full, "--nologo", "--verbosity", "quiet", "--filter", "FullyQualifiedName~Pinion.Generated" };
        var run = await ProcessRunner.RunAsync(
            "dotnet", args, env: env, timeout: TimeSpan.FromSeconds(Math.Max(60, RunTimeoutSeconds)), ct: ct).ConfigureAwait(false);

        if (HasBuildError(run.Combined))
            return new BehaviorDiffReport(full, DateTimeOffset.Now, Array.Empty<BehaviorDiffEntry>(),
                BuildFailed: true, BuildErrors: ExtractCompilerErrors(run.Combined));

        var entries = new List<BehaviorDiffEntry>();
        if (Directory.Exists(outDir))
        {
            foreach (var verified in Directory.GetFiles(outDir, "*.verified.*").OrderBy(p => p, StringComparer.Ordinal))
            {
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
        string file = Path.GetFileName(verifiedPath);
        int dot = file.IndexOf('.');
        string className = dot > 0 ? file[..dot] : file;
        className = Regex.Replace(className, "_CharacterizationTests$", "");
        className = Regex.Replace(className, "_[0-9a-f]{6}$", "");   // overload disambiguation hash
        return className.Replace('_', '.');
    }

    private static bool HasBuildError(string output) =>
        output.Contains("error CS", StringComparison.Ordinal) || output.Contains("Build FAILED", StringComparison.Ordinal);

    private static IReadOnlyList<string> ExtractCompilerErrors(string output) =>
        output.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains("error CS", StringComparison.Ordinal))
            .Distinct().Take(25).ToList();
}
