using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// End-to-end guard for the deterministic generate pipeline: actually synthesize, COMPILE, RUN, and
/// capture the golden master for sample methods, then assert the snapshot CONTENT.
///
/// The source-assertion tests in <see cref="DeterministicSynthesizerTests"/> check the emitted text;
/// they cannot see runtime behaviour. Two of the bugs the NodaTime dogfood surfaced — the null
/// receiver (default(T)! → NullReferenceException) and the throwing-getter return (ParseResult&lt;T&gt;
/// blowing up Verify) — only manifest when the generated test is built and run against the real code.
/// These tests close that gap.
///
/// They drive a real <c>dotnet test</c> on the LegacyShop sample, so they are slower than the rest of
/// the suite and tagged <c>Category=EndToEnd</c> for optional filtering
/// (<c>dotnet test --filter "Category!=EndToEnd"</c> to skip).
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection("LegacyShop e2e")]
public class EndToEndGenerationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pinion.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static CodeUnit UnitAt(string file, string displayName, string methodName)
    {
        var lines = File.ReadAllLines(file);
        int idx = Array.FindIndex(lines, l => l.Contains("public ") && l.Contains(methodName + "("));
        Assert.True(idx >= 0, $"could not find {methodName} in {file}");
        int startLine = idx + 1;
        return new CodeUnit($"S.{displayName}({methodName})", displayName, file, startLine, startLine + 30,
            "sig", Array.Empty<ParamInfo>(), "void", 1, 30,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, true, Array.Empty<string>());
    }

    [Fact]
    public async Task Generated_tests_compile_run_and_capture_real_behavior()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string hardCases = Path.Combine(sampleRoot, "src", "LegacyShop", "HardCases.cs");
        string testProject = Path.Combine(sampleRoot, "tests", "LegacyShop.Tests", "LegacyShop.Tests.csproj");
        string outDir = Path.Combine(Path.GetDirectoryName(testProject)!, "PinionCharacterization");

        // Two real-world shapes the dogfood surfaced, in one batch (a single build/run cycle).
        var render = UnitAt(hardCases, "Formatter.Render", "Render"); // private ctor → static-factory receiver
        var wrap = UnitAt(hardCases, "HardCases.Wrap", "Wrap");       // returns ResultBox (throwing Value getter)

        // Start from a clean slate so we assert freshly captured snapshots, not stale ones.
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        try
        {
            using var gen = new CSharpTestGenerator();
            gen.ConfigureGeneration(testProject);
            var results = await gen.GenerateDeterministicBatchAsync(new[] { render, wrap }, sampleRoot, default);

            // 1. Static-factory receiver: Formatter is obtained via Formatter.Default, so Render runs on a
            //    real instance and records its "#"-prefixed output — not a NullReferenceException.
            var r = results.Single(x => x.Unit.Id == render.Id);
            Assert.True(r.Success, "Render did not characterize: " + string.Join(" | ", r.Diagnostics));
            string renderSnap = await File.ReadAllTextAsync(r.SnapshotPath!);
            Assert.Contains("#", renderSnap);
            Assert.DoesNotContain("NullReferenceException", renderSnap);

            // 2. Throwing getter: the n=-1 input makes ResultBox.Value throw. The snapshot must still be
            //    captured (Success:false recorded) — which only works because the generated test tells
            //    Verify to ignore members that throw. Without that, the whole snapshot fails to capture
            //    and r.Success would be false.
            var w = results.Single(x => x.Unit.Id == wrap.Id);
            Assert.True(w.Success, "Wrap did not characterize: " + string.Join(" | ", w.Diagnostics));
            string wrapSnap = await File.ReadAllTextAsync(w.SnapshotPath!);
            Assert.Contains("Success: false", wrapSnap);
        }
        finally
        {
            if (Directory.Exists(outDir)) { try { Directory.Delete(outDir, recursive: true); } catch { } }
        }
    }

    [Fact]
    public async Task Nondeterministic_method_is_quarantined_with_a_seam_diagnosis()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string hardCases = Path.Combine(sampleRoot, "src", "LegacyShop", "HardCases.cs");
        string testProject = Path.Combine(sampleRoot, "tests", "LegacyShop.Tests", "LegacyShop.Tests.csproj");
        string outDir = Path.Combine(Path.GetDirectoryName(testProject)!, "PinionCharacterization");

        // TicksNow returns DateTime.Now.Ticks — its captured value differs between the capture run and the
        // confirm run, so it must NOT be locked. The pipeline should detect the flake on the confirm run and
        // quarantine it (drop the test + snapshot) with a diagnosis that names the ambient dependency.
        var ticks = UnitAt(hardCases, "HardCases.TicksNow", "TicksNow") with { SeamObstacles = new[] { "DateTime.Now" } };

        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        try
        {
            using var gen = new CSharpTestGenerator();
            gen.ConfigureGeneration(testProject);
            var results = await gen.GenerateDeterministicBatchAsync(new[] { ticks }, sampleRoot, default);

            var r = results.Single();
            Assert.False(r.Success, "a non-deterministic method must not be reported as locked");
            string diag = string.Join(" | ", r.Diagnostics);
            Assert.Contains("non-deterministic", diag, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DateTime.Now", diag); // names the cause from the seam analysis
            // Quarantined: no flaky golden master left behind to fail future verifies.
            Assert.Empty(Directory.GetFiles(outDir, "*TicksNow*.verified.*"));
        }
        finally
        {
            if (Directory.Exists(outDir)) { try { Directory.Delete(outDir, recursive: true); } catch { } }
        }
    }

    [Fact]
    public async Task Tier1_regression_shapes_compile_run_and_capture()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string hardCases = Path.Combine(sampleRoot, "src", "LegacyShop", "HardCases.cs");
        string testProject = Path.Combine(sampleRoot, "tests", "LegacyShop.Tests", "LegacyShop.Tests.csproj");
        string outDir = Path.Combine(Path.GetDirectoryName(testProject)!, "PinionCharacterization");

        // Five shapes that each used to emit a characterization test that did NOT compile (so the target
        // was silently dropped): float params, a mined string with a newline, a non-finite double constant,
        // a ref-string with a null candidate, and a parameter type with a `required` member. All must now
        // compile, run, and capture a golden master in a single batch build/run.
        var units = new[]
        {
            UnitAt(hardCases, "HardCases.Scale", "Scale"),
            UnitAt(hardCases, "HardCases.Multiline", "Multiline"),
            UnitAt(hardCases, "HardCases.Classify", "Classify"),
            UnitAt(hardCases, "HardCases.Relabel", "Relabel"),
            UnitAt(hardCases, "HardCases.Ship", "Ship"),
        };

        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        try
        {
            using var gen = new CSharpTestGenerator();
            gen.ConfigureGeneration(testProject);
            var results = await gen.GenerateDeterministicBatchAsync(units, sampleRoot, default);

            foreach (var r in results)
                Assert.True(r.Success, $"{r.Unit.DisplayName} did not characterize: {string.Join(" | ", r.Diagnostics)}");
        }
        finally
        {
            if (Directory.Exists(outDir)) { try { Directory.Delete(outDir, recursive: true); } catch { } }
        }
    }
}
