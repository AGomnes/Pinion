namespace Pinion.Engine.Analysis;

/// <summary>
/// Recognizes auto-generated source files by path, so the Migration Readiness report ranks the code a
/// team actually wrote — not WinForms/WebForms designers, `.g.cs` build output, or VB `My Project`
/// boilerplate. Path-based (not header-based) so it works identically on the MSBuild and source-scan
/// load paths, and on unrestored legacy code where headers may be absent.
/// </summary>
public static class GeneratedCode
{
    public static bool IsGenerated(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;

        string name = System.IO.Path.GetFileName(filePath);
        if (EndsWithAny(name, ".designer.cs", ".designer.vb", ".g.cs", ".g.i.cs", ".g.vb"))
            return true;

        // VB projects keep generated resources/settings/assembly-info under a "My Project" folder.
        foreach (var segment in filePath.Split('/', '\\'))
            if (segment.Equals("My Project", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool EndsWithAny(string s, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
