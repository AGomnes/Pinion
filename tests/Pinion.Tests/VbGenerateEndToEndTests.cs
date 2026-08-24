using Pinion.Adapters.CSharp.Generate;
using Pinion.Adapters.VisualBasic;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// The VB `generate` payoff, end-to-end: analyze the VB sample with the VB adapter, synthesize C# tests
/// against the VB assembly, COMPILE and RUN them via the real batch pipeline, and capture golden
/// masters. Proves the whole cross-language chain — VB source in, verified behavior lock out.
/// </summary>
[Trait("Category", "EndToEnd")]
public class VbGenerateEndToEndTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pinion.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task Vb_methods_lock_as_golden_masters_via_csharp_tests()
    {
        string sample = Path.Combine(RepoRoot(), "samples", "LegacyVb");
        string testProject = Path.Combine(sample, "tests", "LegacyVb.Tests", "LegacyVb.Tests.csproj");
        string outDir = Path.Combine(Path.GetDirectoryName(testProject)!, "PinionCharacterization");

        var units = await new VisualBasicAdapter().AnalyzeAsync(Path.Combine(sample, "LegacyVb.vbproj"), default);
        var targets = units.Where(u => u.DisplayName is "InvoiceService.CalculateVat" or "InvoiceService.GrossTotal").ToList();
        Assert.Equal(2, targets.Count);

        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        try
        {
            using var gen = new CSharpTestGenerator();
            gen.ConfigureGeneration(testProject);
            var results = await gen.GenerateDeterministicBatchAsync(targets, sample, default);

            foreach (var r in results)
                Assert.True(r.Success, $"{r.Unit.DisplayName} did not characterize: {string.Join(" | ", r.Diagnostics)}");

            // The golden master must hold REAL VB behavior: 9999 * 0.19 ("DE") = 1899.81 — an input row
            // built from constants mined out of the VB Select Case / threshold.
            var vat = results.Single(r => r.Unit.DisplayName == "InvoiceService.CalculateVat");
            string snapshot = await File.ReadAllTextAsync(vat.SnapshotPath!);
            Assert.True(snapshot.Contains("1899.81"), "expected the 9999×0.19 (DE) row — snapshot:\n" + snapshot);
        }
        finally
        {
            if (Directory.Exists(outDir)) { try { Directory.Delete(outDir, recursive: true); } catch { } }
        }
    }
}
