using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Pinion.Engine.Abstractions;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;

namespace Pinion.Adapters.VisualBasic;

/// <summary>
/// The VB.NET language adapter (free `analyze` tier) — proves the <see cref="ILanguageAdapter"/>
/// boundary by producing the same language-neutral IR from VB source via Roslyn's VisualBasic API.
/// Reuses the engine's <see cref="DomainTagger"/> and the same .NET migration-landmine vocabulary as
/// the C# adapter (VB and C# share the framework namespaces).
/// </summary>
/// <remarks>
/// Spike scope: members + cyclomatic complexity + public-entry + domain tags + landmines, via
/// MSBuildWorkspace. Call-graph (blast radius), seams, test-reference detection, and a source-scan
/// fallback for legacy non-SDK .vbproj are the documented follow-ups.
/// </remarks>
public sealed class VisualBasicAdapter : ILanguageAdapter
{
    private readonly Action<string>? _log;

    public VisualBasicAdapter(Action<string>? log = null) => _log = log;

    public string Language => "visualbasic";

    public async Task<IReadOnlyList<CodeUnit>> AnalyzeAsync(string projectOrSolutionPath, CancellationToken ct)
    {
        string path = ResolveInputPath(projectOrSolutionPath);
        bool isProject = path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

        var (solution, workspace) = await LoadSolutionAsync(path, ct).ConfigureAwait(false);
        try
        {
            var units = new List<CodeUnit>();
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                if (project.Language != LanguageNames.VisualBasic) continue;
                // Scope a single-project analyze to that project (don't pull in referenced projects'
                // members). The source-scan project has no file path — it IS the target — so never filter it.
                if (isProject && project.FilePath is not null && !SamePath(project.FilePath, path)) continue;

                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null) continue;

                foreach (var tree in compilation.SyntaxTrees)
                {
                    ct.ThrowIfCancellationRequested();
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                    var imports = FileImports(root);

                    foreach (var (symbol, decl, body) in BehaviorMembers(root, model, ct))
                        units.Add(ToCodeUnit(symbol, decl, body, imports, ct));
                }
            }

            _log?.Invoke($"[analyze] {units.Count} VB production member(s) found.");
            return units;
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    /// <summary>
    /// Load via MSBuild for full reference resolution; if that fails or yields no VB documents (no
    /// MSBuild registered, a legacy non-SDK .vbproj that won't restore), fall back to scanning .vb files
    /// directly so analyze still produces a report. Returns the workspace to dispose.
    /// </summary>
    private async Task<(Solution Solution, Workspace? Workspace)> LoadSolutionAsync(string path, CancellationToken ct)
    {
        try
        {
            var ws = MSBuildWorkspace.Create();
            ws.RegisterWorkspaceFailedHandler(e => _log?.Invoke($"[roslyn] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
            Solution sol = path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                ? await ws.OpenSolutionAsync(path, cancellationToken: ct).ConfigureAwait(false)
                : (await ws.OpenProjectAsync(path, cancellationToken: ct).ConfigureAwait(false)).Solution;

            if (sol.Projects.Any(p => p.Language == LanguageNames.VisualBasic && p.DocumentIds.Count > 0))
            {
                _log?.Invoke("[analyze] resolved VB project via MSBuild (full type resolution).");
                return (sol, ws);
            }
            ws.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[analyze] MSBuild unavailable ({ex.GetType().Name}); scanning VB source — referenced types may not resolve.");
        }

        Solution scan = VbSourceScanLoader.Load(path, _log);
        return (scan, scan.Workspace); // AdhocWorkspace, owned here → dispose
    }

    /// <summary>Methods, functions, and constructors with their declaration + (optional) body block.</summary>
    private static IEnumerable<(IMethodSymbol Symbol, SyntaxNode Decl, MethodBlockBaseSyntax? Body)> BehaviorMembers(
        SyntaxNode root, SemanticModel model, CancellationToken ct)
    {
        foreach (var stmt in root.DescendantNodes().OfType<MethodBaseSyntax>())
        {
            // Sub/Function declarations and constructors (Sub New). Skip Declare/Delegate/event statements.
            if (stmt is not (MethodStatementSyntax or SubNewStatementSyntax)) continue;
            if (model.GetDeclaredSymbol(stmt, ct) is not IMethodSymbol symbol) continue;
            yield return (symbol, stmt, stmt.Parent as MethodBlockBaseSyntax);
        }
    }

    private static CodeUnit ToCodeUnit(
        IMethodSymbol symbol, SyntaxNode decl, MethodBlockBaseSyntax? body, IReadOnlyList<string> imports, CancellationToken ct)
    {
        var span = decl.GetLocation().GetLineSpan();
        int startLine = span.StartLinePosition.Line + 1;
        int endLine = (body ?? decl).GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var parameters = symbol.Parameters.Select(p => new ParamInfo(p.Name, VbSymbols.ShortType(p.Type))).ToList();

        // Domain tags: reuse the engine's language-neutral tagger.
        var tags = DomainTagger.Tag(
            symbol.Name,
            symbol.ContainingType?.Name ?? "",
            parameters.Select(p => p.Type),
            symbol.ReturnsVoid ? "void" : VbSymbols.ShortType(symbol.ReturnType),
            Array.Empty<string>());

        // Landmines: same .NET vocabulary as C#, fed from VB syntax (Imports / Inherits / attributes / file).
        var typeBlock = decl.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        var landmines = VbLandmineDetector.Detect(imports, BaseTypeNames(typeBlock), AttributeNames(decl, typeBlock), span.Path);

        return new CodeUnit(
            Id: VbSymbols.MethodId(symbol),
            DisplayName: VbSymbols.DisplayName(symbol),
            FilePath: span.Path,
            StartLine: startLine,
            EndLine: endLine,
            Signature: VbSymbols.Signature(symbol),
            Parameters: parameters,
            ReturnType: symbol.ReturnsVoid ? "void" : VbSymbols.ShortType(symbol.ReturnType),
            CyclomaticComplexity: VbComplexity.Compute(body),
            LineCount: Math.Max(1, endLine - startLine + 1),
            CallerIds: Array.Empty<string>(),   // call graph: follow-up
            CalleeIds: Array.Empty<string>(),
            DomainTags: tags,
            HasTests: false,                     // test-reference detection: follow-up
            IsPublicEntryPoint: VbSymbols.IsPublicEntryPoint(symbol),
            MigrationLandmines: landmines);
    }

    // ---- VB syntax facts ----

    private static IReadOnlyList<string> FileImports(SyntaxNode root) =>
        root.DescendantNodes().OfType<SimpleImportsClauseSyntax>()
            .Select(c => c.Name.ToString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> BaseTypeNames(TypeBlockSyntax? type)
    {
        if (type is null) return Array.Empty<string>();
        var names = new List<string>();
        foreach (var inh in type.Inherits)
            names.AddRange(inh.Types.Select(SimpleName));
        foreach (var impl in type.Implements)
            names.AddRange(impl.Types.Select(SimpleName));
        return names;
    }

    private static IReadOnlyList<string> AttributeNames(SyntaxNode member, TypeBlockSyntax? type)
    {
        var attrs = member.DescendantNodes().OfType<AttributeSyntax>().Select(a => SimpleName(a.Name));
        if (type?.BlockStatement is not null)
            attrs = attrs.Concat(type.BlockStatement.AttributeLists.SelectMany(l => l.Attributes).Select(a => SimpleName(a.Name)));
        return attrs.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Last segment of a (possibly qualified) type name — "System.Web.UI.Page" → "Page".</summary>
    private static string SimpleName(TypeSyntax t)
    {
        string s = t.ToString();
        int dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : s;
    }

    private static string ResolveInputPath(string input)
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

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
