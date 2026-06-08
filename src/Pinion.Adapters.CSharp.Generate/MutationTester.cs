using System.Text.Json;
using Pinion.Adapters.CSharp;
using Pinion.Generate;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>
/// Runs Stryker.NET against the test project and parses its report into a <see cref="MutationReport"/>.
/// This is how `pinion prove` answers "do the generated tests actually catch regressions?" — it
/// mutates the production code and measures how many mutations the tests kill.
/// </summary>
public sealed class MutationTester
{
    private readonly Action<string>? _log;

    public MutationTester(Action<string>? log = null) => _log = log;

    public async Task<MutationReport?> RunAsync(string testProjectPath, CancellationToken ct)
    {
        string testDir = Path.GetDirectoryName(Path.GetFullPath(testProjectPath))!;
        string outDir = Path.Combine(Path.GetTempPath(), "pinion-mut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);

        try
        {
            string[] args = { "stryker", "--reporter", "json", "--output", outDir, "--verbosity", "error" };
            _log?.Invoke($"[prove] dotnet stryker (mutating the code under {Path.GetFileName(testProjectPath)}) — this can take a while …");

            var result = await ProcessRunner.RunAsync("dotnet", args, workingDirectory: testDir,
                timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

            if (result.TimedOut) { _log?.Invoke("[prove] Stryker timed out."); return null; }

            string? report = Directory.GetFiles(outDir, "mutation-report.json", SearchOption.AllDirectories).FirstOrDefault();
            if (report is null)
            {
                _log?.Invoke("[prove] no Stryker report produced. Is dotnet-stryker installed? (`dotnet tool install dotnet-stryker`)");
                if (result.Combined.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    _log?.Invoke("[prove] `dotnet stryker` was not found — install it as a local or global tool.");
                return null;
            }

            return Parse(report);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[prove] mutation testing failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    internal static MutationReport Parse(string reportPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var files = new List<MutationFileResult>();
        int killed = 0, survived = 0, nocov = 0, timeout = 0;

        if (doc.RootElement.TryGetProperty("files", out var filesObj) && filesObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in filesObj.EnumerateObject())
            {
                int k = 0, s = 0, n = 0, t = 0;
                if (file.Value.TryGetProperty("mutants", out var mutants) && mutants.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mutants.EnumerateArray())
                    {
                        switch (m.TryGetProperty("status", out var st) ? st.GetString() : null)
                        {
                            case "Killed": k++; break;
                            case "Survived": s++; break;
                            case "NoCoverage": n++; break;
                            case "Timeout": t++; break;
                        }
                    }
                }
                if (k + s + n + t == 0) continue;
                killed += k; survived += s; nocov += n; timeout += t;
                files.Add(new MutationFileResult(Path.GetFileName(file.Name.Replace('\\', '/')), k, s, n, t));
            }
        }

        return new MutationReport(killed, survived, nocov, timeout,
            files.OrderByDescending(f => f.Tested).ToList());
    }
}
