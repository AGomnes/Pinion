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
        var files = EnumerateVbFiles(root).ToList();

        log?.Invoke($"[analyze] VB source scan: {files.Count} .vb file(s) under {root}.");

        var refs = RuntimeReferences();
        var workspace = new AdhocWorkspace();
        Solution solution = workspace.CurrentSolution;

        var projId = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(
            projId, VersionStamp.Default, "ScannedVbSources", "ScannedVbSources",
            LanguageNames.VisualBasic, metadataReferences: refs));

        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projId), Path.GetFileName(file), SourceText.From(text), filePath: file);
        }

        return solution;
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

    private static IReadOnlyList<MetadataReference> RuntimeReferences()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa || tpa.Length == 0)
            return Array.Empty<MetadataReference>();

        var refs = new List<MetadataReference>();
        foreach (var p in tpa.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(p)) continue;
            try { refs.Add(MetadataReference.CreateFromFile(p)); }
            catch { /* skip unreadable */ }
        }
        return refs;
    }
}
