using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Pinion.Engine.Analysis;
using Pinion.Engine.Reporting;

namespace Pinion.Adapters.CSharp;

/// <summary>One file's planned seam rewrite: the full before/after text plus a human-readable diff.</summary>
public sealed record SeamPlan(
    string FilePath,
    string OriginalText,
    string NewText,
    string Diff,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Reads);

/// <summary>The outcome of planning: rewrites to apply + methods that need a manual seam (with why).</summary>
public sealed record SeamPlanResult(IReadOnlyList<SeamPlan> Plans, IReadOnlyList<string> Skipped);

/// <summary>
/// Drives `pinion seam`: loads the code, plans the wrapper+overload rewrites (<see cref="SeamRewriter"/>),
/// and applies them with a compile gate — if the project no longer builds after writing, every file is
/// reverted. Editing user source is the highest-trust action in the product, so the flow is
/// preview-by-default, minimal-diff, and never leaves a broken tree behind.
/// </summary>
public sealed class SeamApplier : IDisposable
{
    private readonly Action<string>? _log;
    private readonly bool _tryMsBuild;
    private readonly List<Workspace> _workspaces = new();

    public SeamApplier(Action<string>? log = null, bool tryMsBuild = false)
    {
        _log = log;
        _tryMsBuild = tryMsBuild;
    }

    public void Dispose()
    {
        foreach (var ws in _workspaces) ws.Dispose();
        _workspaces.Clear();
    }

    /// <summary>Plan seam rewrites for every eligible method (optionally filtered by a name substring
    /// matching the method or its containing type). Touches nothing on disk.</summary>
    public async Task<SeamPlanResult> PlanAsync(string path, string? target, CancellationToken ct)
    {
        var solution = await LoadAsync(path, ct).ConfigureAwait(false);

        Func<MethodDeclarationSyntax, bool>? include = null;
        if (!string.IsNullOrWhiteSpace(target))
        {
            include = m =>
                m.Identifier.ValueText.Contains(target!, StringComparison.OrdinalIgnoreCase)
                || (m.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "")
                    .Contains(target!, StringComparison.OrdinalIgnoreCase);
        }

        var plans = new List<SeamPlan>();
        var skipped = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            foreach (var doc in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (doc.FilePath is null || GeneratedCode.IsGenerated(doc.FilePath) || !seenFiles.Add(doc.FilePath)) continue;

                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (model is null || root is null) continue;

                var (newRoot, seamed, docSkipped) = SeamRewriter.RewriteDocument(root, model, include, ct);
                foreach (var (method, reason) in docSkipped) skipped.Add($"{method} — {reason}");
                if (seamed.Count == 0) continue;

                string original = root.ToFullString();
                string rewritten = newRoot.ToFullString();
                if (rewritten == original) continue;

                plans.Add(new SeamPlan(
                    doc.FilePath, original, rewritten,
                    BehaviorDiff.Unified(original, rewritten),
                    seamed.Select(s => s.Method).ToList(),
                    seamed.SelectMany(s => s.Reads).Distinct().ToList()));
            }
        }

        return new SeamPlanResult(plans, skipped);
    }

    /// <summary>
    /// Write the planned rewrites, then compile-gate them: build the target and, if the build now fails,
    /// revert every written file. Returns the build errors on failure. When the input has no buildable
    /// project (a bare source directory), the gate is skipped with a note.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<string> Errors)> ApplyAsync(
        string path, IReadOnlyList<SeamPlan> plans, CancellationToken ct)
    {
        foreach (var p in plans)
            File.WriteAllText(p.FilePath, p.NewText);

        string? buildTarget = TryResolveBuildTarget(path);
        if (buildTarget is null)
        {
            _log?.Invoke("[seam] no .csproj/.sln found — compile gate skipped (verify the build yourself).");
            return (true, new[] { "note: no buildable project found, compile gate skipped" });
        }

        _log?.Invoke($"[seam] compile gate: dotnet build {Path.GetFileName(buildTarget)} …");
        var run = await ProcessRunner.RunAsync(
            "dotnet", new[] { "build", buildTarget, "--nologo", "--verbosity", "quiet" },
            timeout: TimeSpan.FromSeconds(600), ct: ct).ConfigureAwait(false);

        bool broke = run.TimedOut || run.Combined.Contains("error CS", StringComparison.Ordinal);
        if (!broke) return (true, Array.Empty<string>());

        foreach (var p in plans)
        {
            try { File.WriteAllText(p.FilePath, p.OriginalText); } catch {  }
        }
        var errors = run.TimedOut
            ? new[] { "build timed out — all seam edits were reverted" }
            : run.Combined.Split('\n').Select(l => l.Trim())
                .Where(l => l.Contains("error CS", StringComparison.Ordinal)).Distinct().Take(10).ToArray();
        return (false, errors);
    }

    private async Task<Solution> LoadAsync(string path, CancellationToken ct)
    {
        if (_tryMsBuild)
        {
            try
            {
                var ws = MSBuildWorkspace.Create();
                ws.RegisterWorkspaceFailedHandler(e => _log?.Invoke($"[roslyn] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
                string target = CSharpAdapter.ResolveInputPath(path);
                var sol = await CSharpAdapter.OpenViaMSBuildAsync(ws, target, ct).ConfigureAwait(false);
                if (CSharpAdapter.HasCSharpDocuments(sol))
                {
                    _workspaces.Add(ws);
                    return sol;
                }
                ws.Dispose();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[seam] MSBuild unavailable ({ex.GetType().Name}); scanning source directly.");
            }
        }

        var scanned = SourceScanLoader.Load(path, _log);
        _workspaces.Add(scanned.Workspace);
        return scanned;
    }

    private static string? TryResolveBuildTarget(string input)
    {
        try { return CSharpAdapter.ResolveInputPath(input); }
        catch { return null; }
    }
}
