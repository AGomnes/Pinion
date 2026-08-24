using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// A no-MSBuild fallback: build a Roslyn <see cref="Solution"/> straight from the .cs
/// files on disk. Project loading depends on a healthy MSBuild (and, for legacy
/// non-SDK projects, a working .NET Framework build host) that real client machines
/// often lack — but Pinion's analysis is largely syntactic + source-level semantic, so
/// it can still produce a useful report from raw source. This is what lets the tool
/// promise it runs on *any* pile of C#.
/// </summary>
internal static class SourceScanLoader
{
    private static readonly string[] ExcludedDirs =
        { "bin", "obj", ".git", ".vs", "node_modules", "packages", "TestResults" };

    public static Solution Load(string targetPath, Action<string>? log)
    {
        string root = ResolveRoot(targetPath);
        var files = EnumerateCsharpFiles(root).ToList();

        var production = new List<string>();
        var tests = new List<string>();
        foreach (var f in files)
            (IsTestFile(f) ? tests : production).Add(f);

        log?.Invoke($"[analyze] source scan: {production.Count} source file(s), {tests.Count} test file(s) under {root}.");

        var refs = RuntimeReferences();
        var workspace = new AdhocWorkspace();
        Solution solution = workspace.CurrentSolution;

        var prodId = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(
            prodId, VersionStamp.Default, "ScannedSources", "ScannedSources",
            LanguageNames.CSharp, metadataReferences: refs));
        solution = AddDocuments(solution, prodId, production);
        solution = AddImplicitUsings(solution, prodId);

        if (tests.Count > 0)
        {
            var testId = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                testId, VersionStamp.Default, "ScannedTests", "ScannedTests",
                LanguageNames.CSharp, metadataReferences: refs,
                projectReferences: new[] { new ProjectReference(prodId) }));
            solution = AddDocuments(solution, testId, tests);
            solution = AddImplicitUsings(solution, testId);
        }

        return solution;
    }

    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    private static Solution AddImplicitUsings(Solution solution, ProjectId projectId) =>
        solution.AddDocument(DocumentId.CreateNewId(projectId), "PinionGlobalUsings.cs", SourceText.From(ImplicitUsings));

    private static Solution AddDocuments(Solution solution, ProjectId projectId, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(file),
                SourceText.From(text),
                filePath: file);
        }
        return solution;
    }

    private static string ResolveRoot(string targetPath)
    {
        if (Directory.Exists(targetPath)) return Path.GetFullPath(targetPath);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        return dir ?? Directory.GetCurrentDirectory();
    }

    private static IEnumerable<string> EnumerateCsharpFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsInExcludedDir(p, root));

    private static bool IsInExcludedDir(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => ExcludedDirs.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsTestFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => s.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            string head = File.ReadAllText(path);
            if (head.Contains("using Xunit", StringComparison.Ordinal)
                || head.Contains("using NUnit", StringComparison.Ordinal)
                || head.Contains("Microsoft.VisualStudio.TestTools", StringComparison.Ordinal))
                return true;
        }
        catch {  }

        return false;
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
            catch {  }
        }
        return refs;
    }
}
