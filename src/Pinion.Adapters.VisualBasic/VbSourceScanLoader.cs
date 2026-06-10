using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Pinion.Adapters.VisualBasic;

/// <summary>
/// A no-MSBuild fallback: build a Roslyn <see cref="Solution"/> straight from the <c>.vb</c> files on
/// disk. This is what makes VB analyze useful on the code that actually matters — legacy non-SDK
/// <c>.vbproj</c> (WebForms line-of-business apps) that won't restore or MSBuild-load on a modern box.
/// VB analysis is largely syntactic + name-based (complexity, Imports-driven landmines, domain tags),
/// so a useful readiness report comes out of raw source even without full reference resolution.
/// Mirrors the C# adapter's SourceScanLoader.
/// </summary>
internal static class VbSourceScanLoader
{
    private static readonly string[] ExcludedDirs =
        { "bin", "obj", ".git", ".vs", "node_modules", "packages", "TestResults", "My Project" };

    public static Solution Load(string targetPath, Action<string>? log)
    {
        string root = ResolveRoot(targetPath);

        var production = new List<string>();
        var tests = new List<string>();
        foreach (var f in EnumerateVbFiles(root))
            (IsTestFile(f) ? tests : production).Add(f);

        log?.Invoke($"[analyze] VB source scan: {production.Count} source file(s), {tests.Count} test file(s) under {root}.");

        var refs = RuntimeReferences();
        var workspace = new AdhocWorkspace();
        Solution solution = workspace.CurrentSolution;

        var prodId = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(
            prodId, VersionStamp.Default, "ScannedVbSources", "ScannedVbSources",
            LanguageNames.VisualBasic, metadataReferences: refs));
        solution = AddDocuments(solution, prodId, production);

        if (tests.Count > 0)
        {
            // Name ends in "Tests" so the adapter's IsTestProject heuristic catches it, and it references
            // the production project so test calls resolve to production symbols (→ HasTests).
            var testId = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                testId, VersionStamp.Default, "ScannedVbTests", "ScannedVbTests",
                LanguageNames.VisualBasic, metadataReferences: refs,
                projectReferences: new[] { new ProjectReference(prodId) }));
            solution = AddDocuments(solution, testId, tests);
        }

        return solution;
    }

    private static Solution AddDocuments(Solution solution, ProjectId projectId, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), Path.GetFileName(file), SourceText.From(text), filePath: file);
        }
        return solution;
    }

    private static bool IsTestFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (VbSymbols.LooksLikeTestName(name))
            return true;
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => s.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            string head = File.ReadAllText(path);
            if (head.Contains("Imports Xunit", StringComparison.OrdinalIgnoreCase)
                || head.Contains("Imports NUnit", StringComparison.OrdinalIgnoreCase)
                || head.Contains("Microsoft.VisualStudio.TestTools", StringComparison.Ordinal))
                return true;
        }
        catch { /* ignore */ }
        return false;
    }

    private static string ResolveRoot(string targetPath)
    {
        if (Directory.Exists(targetPath)) return Path.GetFullPath(targetPath);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        return dir ?? Directory.GetCurrentDirectory();
    }

    private static IEnumerable<string> EnumerateVbFiles(string root) =>
        Directory.EnumerateFiles(root, "*.vb", SearchOption.AllDirectories)
            .Where(p => !IsInExcludedDir(p, root));

    private static bool IsInExcludedDir(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => ExcludedDirs.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    // Test-framework assemblies must NOT be referenced by the scanned production project: the adapter's
    // IsTestProject classifies on these names, so leaving them in (they can appear in the host's TPA when
    // Pinion itself runs under a test runner) would mark every scanned source project as a test project →
    // zero analyzed members. Test detection still works via the "ScannedVbTests" project name.
    private static readonly string[] TestFrameworkMarkers =
        { "xunit", "nunit", "mstest", "testplatform", "testhost", "Microsoft.VisualStudio.TestTools" };

    private static IReadOnlyList<MetadataReference> RuntimeReferences()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa || tpa.Length == 0)
            return Array.Empty<MetadataReference>();

        var refs = new List<MetadataReference>();
        foreach (var p in tpa.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(p)) continue;
            string file = Path.GetFileName(p);
            if (TestFrameworkMarkers.Any(m => file.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
            try { refs.Add(MetadataReference.CreateFromFile(p)); }
            catch { /* skip unreadable */ }
        }
        return refs;
    }
}
