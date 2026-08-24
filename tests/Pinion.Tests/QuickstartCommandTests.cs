using Pinion.Engine.Model;
using Pinion.Engine.Scaffolding;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// Unit coverage for the `quickstart` golden-path decision logic (<see cref="QuickstartPlanner"/>) —
/// target selection and test-project planning — without driving a real build/run (that path is covered by
/// the deterministic generate e2e tests). The full command was also smoke-run against the LegacyShop sample.
/// </summary>
public class QuickstartCommandTests
{
    private static CodeUnit Unit(string name, int complexity, bool hasTests, bool publicEntry) =>
        new($"S.{name}()", name, $"{name}.cs", 1, 30, "sig",
            Array.Empty<ParamInfo>(), "void", complexity, 30,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            HasTests: hasTests, IsPublicEntryPoint: publicEntry, Array.Empty<string>());

    [Fact]
    public void SelectRiskiest_picks_untested_public_entry_points_ordered_by_risk()
    {
        var units = new[]
        {
            Unit("A.VeryRisky", complexity: 25, hasTests: false, publicEntry: true),
            Unit("B.SomewhatRisky", complexity: 8, hasTests: false, publicEntry: true),
            Unit("C.Tested", complexity: 40, hasTests: true, publicEntry: true),
            Unit("D.Internal", complexity: 30, hasTests: false, publicEntry: false),
        };

        var picked = QuickstartPlanner.SelectRiskiest("proj", units, top: 10);

        Assert.Equal(new[] { "A.VeryRisky", "B.SomewhatRisky" }, picked.Select(u => u.DisplayName));
    }

    [Fact]
    public void SelectRiskiest_respects_the_top_cap()
    {
        var units = Enumerable.Range(0, 5)
            .Select(i => Unit($"M{i}", complexity: 10 + i, hasTests: false, publicEntry: true))
            .ToArray();

        Assert.Equal(2, QuickstartPlanner.SelectRiskiest("proj", units, top: 2).Count);
    }

    [Fact]
    public void PlanTestProject_scaffolds_a_conventional_project_next_to_a_single_csproj()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "Shop.csproj"), "<Project/>");

        var plan = QuickstartPlanner.PlanTestProject(tmp.Path, testProjectPath: null, tfm: "net8.0");

        Assert.Null(plan.Error);
        Assert.False(plan.Exists);
        Assert.EndsWith(Path.Combine("Shop.CharacterizationTests", "Shop.CharacterizationTests.csproj"), plan.ProjectFile);
        Assert.NotNull(plan.RelativeCodeRef);
        Assert.Contains("Shop.csproj", plan.RelativeCodeRef!);
    }

    [Fact]
    public void PlanTestProject_errors_when_the_code_project_is_ambiguous()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "One.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(tmp.Path, "Two.csproj"), "<Project/>");

        var plan = QuickstartPlanner.PlanTestProject(tmp.Path, testProjectPath: null, tfm: "net8.0");

        Assert.NotNull(plan.Error);
    }

    [Fact]
    public void PlanTestProject_uses_an_existing_supplied_test_project()
    {
        using var tmp = new TempDir();
        string existing = Path.Combine(tmp.Path, "My.Tests.csproj");
        File.WriteAllText(existing, "<Project/>");

        var plan = QuickstartPlanner.PlanTestProject(tmp.Path, existing, tfm: "net8.0");

        Assert.Null(plan.Error);
        Assert.True(plan.Exists);
        Assert.Equal(existing, plan.ProjectFile);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pinion-qs-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
