using System.Text.RegularExpressions;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;

namespace Pinion.Engine.Scaffolding;

/// <summary>
/// The decision logic behind the one-command `quickstart` golden path: which methods to lock first, and
/// where the host test project should live. Lives in the engine (not the CLI) so it's reusable and
/// unit-testable without the CLI's MSBuild dependencies.
/// </summary>
public static class QuickstartPlanner
{
    /// <summary>Where a host test project should live, and (when it must be scaffolded) the relative
    /// path back to the code project it should reference.</summary>
    public readonly record struct TestProjectPlan(
        string ProjectFile, bool Exists, string? RelativeCodeRef, string? Error);

    /// <summary>The highest-risk methods worth locking first: untested public entry points, risk-ranked.
    /// Mirrors `generate`'s default target selection so quickstart and generate agree on "riskiest".</summary>
    public static List<CodeUnit> SelectRiskiest(string path, IReadOnlyList<CodeUnit> units, int top)
    {
        var report = ReportBuilder.Build(path, units);
        return report.Hotspots
            .Where(s => !s.Unit.HasTests && s.Unit.IsPublicEntryPoint)
            .Take(Math.Max(1, top))
            .Select(s => s.Unit)
            .ToList();
    }

    /// <summary>Resolve the host test project: an explicit one if supplied, otherwise a conventional
    /// <c>&lt;Code&gt;.CharacterizationTests</c> project next to the (single) code project — to be
    /// scaffolded on demand. Returns an Error when a project can't be unambiguously located.</summary>
    public static TestProjectPlan PlanTestProject(string path, string? testProjectPath, string tfm)
    {
        if (testProjectPath is not null)
        {
            string p = Path.GetFullPath(testProjectPath);
            if (File.Exists(p)) return new TestProjectPlan(p, Exists: true, null, null);
            string? rel = RelativeCodeRefFor(path, Path.GetDirectoryName(p)!);
            return rel is null
                ? new TestProjectPlan(p, false, null,
                    $"--test-project '{p}' doesn't exist and the code project to reference couldn't be resolved from '{path}'. Point quickstart at a single .csproj.")
                : new TestProjectPlan(p, false, rel, null);
        }

        string? codeCsproj = ResolveSingleCsproj(path);
        if (codeCsproj is null)
            return new TestProjectPlan("", false, null,
                $"couldn't resolve a single .csproj from '{path}'. Point quickstart at one .csproj, or pass --test-project to host the tests.");

        string name = Regex.Replace(Path.GetFileNameWithoutExtension(codeCsproj) + ".CharacterizationTests", @"[^A-Za-z0-9_.]", "_");
        string projDir = Path.Combine(Path.GetDirectoryName(codeCsproj)!, name);
        string projFile = Path.Combine(projDir, name + ".csproj");
        if (File.Exists(projFile))
            return new TestProjectPlan(projFile, Exists: true, null, null);

        return new TestProjectPlan(projFile, Exists: false, Path.GetRelativePath(projDir, codeCsproj), null);
    }

    /// <summary>The code .csproj a (to-be-created) test project at <paramref name="testProjDir"/> should
    /// reference, as a relative path — or null if <paramref name="path"/> isn't a single resolvable project.</summary>
    private static string? RelativeCodeRefFor(string path, string testProjDir)
    {
        string? code = ResolveSingleCsproj(path);
        return code is null ? null : Path.GetRelativePath(testProjDir, code);
    }

    /// <summary>A .csproj path, or a directory containing exactly one .csproj, resolved to a full path.</summary>
    public static string? ResolveSingleCsproj(string input)
    {
        if (File.Exists(input) && input.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(input);
        if (Directory.Exists(input))
        {
            var found = Directory.GetFiles(Path.GetFullPath(input), "*.csproj");
            if (found.Length == 1) return found[0];
        }
        return null;
    }
}
