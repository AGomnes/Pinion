using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Pinion.Adapters.VisualBasic;

/// <summary>
/// Solution loading for the generate tier's VB path: MSBuild when the host registered it (full
/// reference resolution — what lets the synthesizer construct real values), else the same source-scan
/// fallback `analyze` uses. Public because the C# generate adapter drives VB synthesis (it emits C#
/// tests against the VB assembly); the analyze-tier loader stays private to the adapter.
/// </summary>
public static class VbGenerateLoader
{
    /// <summary>Load the VB solution for <paramref name="path"/> (a .sln/.slnx/.vbproj or a directory
    /// holding one). Returns the workspace the caller must dispose.</summary>
    public static async Task<(Solution Solution, Workspace Workspace)> LoadAsync(
        string path, bool tryMsBuild, Action<string>? log, CancellationToken ct)
    {
        string target = ResolveInput(path);

        if (tryMsBuild)
        {
            MSBuildWorkspace? ws = null;
            try
            {
                ws = MSBuildWorkspace.Create();
                ws.RegisterWorkspaceFailedHandler(e => log?.Invoke($"[roslyn] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
                Solution sol = target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                    ? await ws.OpenSolutionAsync(target, cancellationToken: ct).ConfigureAwait(false)
                    : (await ws.OpenProjectAsync(target, cancellationToken: ct).ConfigureAwait(false)).Solution;

                if (sol.Projects.Any(p => p.Language == LanguageNames.VisualBasic && p.DocumentIds.Count > 0))
                {
                    log?.Invoke("[generate] resolved VB project via MSBuild (full type resolution).");
                    return (sol, ws);
                }
                ws.Dispose();
            }
            catch (Exception ex)
            {
                ws?.Dispose();
                log?.Invoke($"[generate] MSBuild unavailable for VB ({ex.GetType().Name}); scanning source — referenced types may not resolve.");
            }
        }

        Solution scan = VbSourceScanLoader.Load(target, log);
        return (scan, scan.Workspace);
    }

    private static string ResolveInput(string input)
    {
        if (File.Exists(input)) return Path.GetFullPath(input);
        if (Directory.Exists(input))
        {
            string dir = Path.GetFullPath(input);
            var sln = Directory.GetFiles(dir, "*.sln").Concat(Directory.GetFiles(dir, "*.slnx")).FirstOrDefault();
            if (sln is not null) return sln;
            var vbproj = Directory.GetFiles(dir, "*.vbproj");
            if (vbproj.Length == 1) return vbproj[0];
            if (vbproj.Length > 1) throw new InvalidOperationException($"Multiple .vbproj in '{dir}'. Point at a specific one.");
            throw new FileNotFoundException($"No .sln or .vbproj found in '{dir}'.");
        }
        throw new FileNotFoundException($"Path not found: '{input}'.");
    }
}
