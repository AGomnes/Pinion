using System.Diagnostics;
using System.Xml.Linq;
using Pinion.Engine.Analysis;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Runs the target's test suite under Coverlet and parses the resulting Cobertura
/// report into a <see cref="CoverageSummary"/>. This is opt-in (it executes tests, so
/// it is slower and can fail) and always non-fatal: if anything goes wrong we log and
/// return null so `analyze` still produces its static report.
/// </summary>
public sealed class CoverageCollector
{
    private readonly Action<string>? _log;

    public CoverageCollector(Action<string>? log = null) => _log = log;

    public async Task<CoverageSummary?> CollectAsync(string targetPath, CancellationToken ct)
    {
        string resultsDir = Path.Combine(Path.GetTempPath(), "pinion-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDir);

        try
        {
            int exit = await RunDotNetTestAsync(targetPath, resultsDir, ct).ConfigureAwait(false);
            // Some tests failing doesn't invalidate coverage data, but no data does.
            if (exit != 0)
                _log?.Invoke($"[coverage] `dotnet test` exited {exit}; using whatever coverage was produced.");

            var coberturaFiles = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
            if (coberturaFiles.Length == 0)
            {
                _log?.Invoke("[coverage] No Cobertura report produced. Is coverlet.collector referenced by the test projects?");
                return null;
            }

            return Aggregate(coberturaFiles);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[coverage] skipped: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(resultsDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<int> RunDotNetTestAsync(string targetPath, string resultsDir, CancellationToken ct)
    {
        string[] args =
        {
            "test", targetPath, "--collect:XPlat Code Coverage",
            "--results-directory", resultsDir, "--nologo", "--verbosity", "quiet",
        };
        _log?.Invoke($"[coverage] dotnet test {targetPath} --collect \"XPlat Code Coverage\" …");

        // The whole suite can be slow; cap it generously so a hung test can't wedge the run.
        var result = await ProcessRunner.RunAsync("dotnet", args, timeout: TimeSpan.FromMinutes(10), ct: ct)
            .ConfigureAwait(false);
        if (result.TimedOut) _log?.Invoke("[coverage] dotnet test timed out and was terminated.");
        return result.ExitCode;
    }

    /// <summary>
    /// Sum the per-report totals. Coverlet writes lines-/branches-covered/valid on the
    /// root &lt;coverage&gt; element of each Cobertura file (one per test project).
    /// </summary>
    private static CoverageSummary Aggregate(IEnumerable<string> coberturaFiles)
    {
        int coveredLines = 0, totalLines = 0, coveredBranches = 0, totalBranches = 0;

        foreach (var file in coberturaFiles)
        {
            var root = XDocument.Load(file).Root;
            if (root is null) continue;

            coveredLines += IntAttr(root, "lines-covered");
            totalLines += IntAttr(root, "lines-valid");
            coveredBranches += IntAttr(root, "branches-covered");
            totalBranches += IntAttr(root, "branches-valid");
        }

        return new CoverageSummary(coveredLines, totalLines, coveredBranches, totalBranches);
    }

    private static int IntAttr(XElement el, string name) =>
        int.TryParse(el.Attribute(name)?.Value, out int v) ? v : 0;
}
