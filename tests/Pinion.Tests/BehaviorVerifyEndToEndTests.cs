using Pinion.Adapters.CSharp;
using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Model;
using Pinion.Engine.Reporting;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// End-to-end proof of the verify loop: lock behavior with the real generate pipeline, confirm
/// `verify` reports it identical against unchanged code, then simulate a behavior change and confirm
/// it's detected with a diff. Drives a real `dotnet test`, so it's tagged Category=EndToEnd.
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection("LegacyShop e2e")]
public class BehaviorVerifyEndToEndTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pinion.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static CodeUnit UnitAt(string file, string displayName, string methodName)
    {
        var lines = File.ReadAllLines(file);
        int idx = Array.FindIndex(lines, l => l.Contains("public ") && l.Contains(methodName + "("));
        Assert.True(idx >= 0, $"could not find {methodName} in {file}");
        int start = idx + 1;
        return new CodeUnit($"S.{displayName}({methodName})", displayName, file, start, start + 30,
            "sig", Array.Empty<ParamInfo>(), "void", 1, 30,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, true, Array.Empty<string>());
    }

    [Fact]
    public async Task Verify_reports_identical_then_detects_a_simulated_behavior_change()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string hardCases = Path.Combine(sampleRoot, "src", "LegacyShop", "HardCases.cs");
        string testProject = Path.Combine(sampleRoot, "tests", "LegacyShop.Tests", "LegacyShop.Tests.csproj");
        string outDir = Path.Combine(Path.GetDirectoryName(testProject)!, "PinionCharacterization");

        var render = UnitAt(hardCases, "Formatter.Render", "Render");

        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        try
        {
            using (var gen = new CSharpTestGenerator())
            {
                gen.ConfigureGeneration(testProject);
                var results = await gen.GenerateDeterministicBatchAsync(new[] { render }, sampleRoot, default);
                Assert.True(results.Single().Success, string.Join(" | ", results.Single().Diagnostics));
            }

            var clean = await new BehaviorVerifier().VerifyAsync(testProject);
            Assert.False(clean.BuildFailed);
            Assert.True(clean.BehaviorPreserved, "expected identical behavior, got: " +
                string.Join("; ", clean.Entries.Where(e => e.Status == BehaviorChange.Changed).Select(e => e.Name)));

            string master = Directory.GetFiles(outDir, "*.verified.*").Single();
            File.WriteAllText(master, File.ReadAllText(master).Replace("#0", "#CHANGED"));

            var changed = await new BehaviorVerifier().VerifyAsync(testProject);
            Assert.False(changed.BuildFailed);
            Assert.Equal(1, changed.Changed);
            var entry = changed.Entries.Single(e => e.Status == BehaviorChange.Changed);
            Assert.Contains("#CHANGED", entry.Diff);
            Assert.Contains("#0", entry.Diff);
        }
        finally
        {
            if (Directory.Exists(outDir)) { try { Directory.Delete(outDir, recursive: true); } catch { } }
        }
    }
}
