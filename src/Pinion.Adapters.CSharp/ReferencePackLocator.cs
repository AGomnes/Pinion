using System.Runtime.InteropServices;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Finds the reference assemblies for a target framework on the local machine.
///
/// These are the ground truth for "does this API exist on .NET N", which is why compatibility checking
/// needs no curated catalog: the reference pack ships with every SDK install, is exact for the version
/// the user actually targets, and never goes stale. A hand-maintained list of removed APIs would be
/// wrong the day the next .NET ships.
/// </summary>
internal static class ReferencePackLocator
{
    /// <summary>Reference-assembly directory for <paramref name="tfm"/> (e.g. "net10.0"), or null when
    /// that framework is not installed. Highest installed patch wins.</summary>
    public static string? Locate(string tfm)
    {
        if (!TryParseVersion(tfm, out int major)) return null;

        foreach (string root in PackRoots())
        {
            string packDir = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packDir)) continue;

            var best = Directory.GetDirectories(packDir)
                .Select(d => (Dir: d, Ver: ParseFolderVersion(Path.GetFileName(d))))
                .Where(x => x.Ver is not null && x.Ver.Major == major)
                .OrderByDescending(x => x.Ver)
                .Select(x => x.Dir)
                .FirstOrDefault();
            if (best is null) continue;

            string refDir = Path.Combine(best, "ref", tfm);
            if (Directory.Exists(refDir)) return refDir;

            // Some layouts nest the moniker differently; take the only ref subdirectory if unambiguous.
            string refRoot = Path.Combine(best, "ref");
            if (Directory.Exists(refRoot))
            {
                var subs = Directory.GetDirectories(refRoot);
                if (subs.Length == 1) return subs[0];
            }
        }

        return null;
    }

    /// <summary>Target frameworks with reference assemblies present on this machine, newest first.</summary>
    public static IReadOnlyList<string> Installed()
    {
        var found = new SortedSet<int>();
        foreach (string root in PackRoots())
        {
            string packDir = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packDir)) continue;
            foreach (string d in Directory.GetDirectories(packDir))
                if (ParseFolderVersion(Path.GetFileName(d)) is { } v)
                    found.Add(v.Major);
        }
        return found.Reverse().Select(m => $"net{m}.0").ToList();
    }

    /// <summary>Candidate dotnet root directories, in priority order.</summary>
    private static IEnumerable<string> PackRoots()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } envRoot)
            yield return envRoot;

        // The running host lives under <dotnet-root>/shared/Microsoft.NETCore.App/<version>, so walking
        // up three levels lands on the root regardless of where the SDK was installed.
        string? dir = Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory()?.TrimEnd(Path.DirectorySeparatorChar));
        for (int i = 0; i < 2 && dir is not null; i++) dir = Path.GetDirectoryName(dir);
        if (dir is not null) yield return dir;

        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\dotnet";
            yield return @"C:\Program Files (x86)\dotnet";
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/local/share/dotnet";
        }
    }

    private static bool TryParseVersion(string tfm, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(tfm) || !tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = tfm[3..];
        int dot = rest.IndexOf('.');
        return int.TryParse(dot >= 0 ? rest[..dot] : rest, out major) && major >= 5;
    }

    private static Version? ParseFolderVersion(string name) =>
        Version.TryParse(name.Split('-')[0], out var v) ? v : null;
}
