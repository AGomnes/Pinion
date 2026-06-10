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
            return await AnalyzeSolutionAsync(solution, isProject ? path : null, ct).ConfigureAwait(false);
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    /// <summary>The language-agnostic core, over an already-loaded solution (MSBuild or source-scan):
    /// gather production members → call graph + tags → IR. Internal so it can be tested directly with an
    /// in-memory solution, independent of how the solution was loaded.</summary>
    internal async Task<IReadOnlyList<CodeUnit>> AnalyzeSolutionAsync(Solution solution, string? targetProjectPath, CancellationToken ct)
    {
        {
            // Which production members any test project references — turns "untested" into a fact.
            var testedIds = await CollectTestedMethodIdsAsync(solution, ct).ConfigureAwait(false);

            // Pass 1: gather every VB production member with its model + imports.
            var raw = new List<RawVb>();
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                if (project.Language != LanguageNames.VisualBasic) continue;
                // Scope a single-project analyze to that project (don't pull in referenced projects'
                // members). The source-scan project has no file path — it IS the target — so never filter it.
                if (targetProjectPath is not null && project.FilePath is not null && !SamePath(project.FilePath, targetProjectPath)) continue;

                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is null || IsTestProject(project, compilation)) continue;

                foreach (var tree in compilation.SyntaxTrees)
                {
                    ct.ThrowIfCancellationRequested();
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                    var imports = FileImports(root);

                    foreach (var (symbol, decl, body) in BehaviorMembers(root, model, ct))
                        raw.Add(new RawVb(symbol, decl, body, model, imports, VbSymbols.MethodId(symbol)));
                }
            }

            var byId = new Dictionary<string, RawVb>(StringComparer.Ordinal);
            foreach (var m in raw) byId.TryAdd(m.Id, m); // first declaration wins (partials/overloads share an id rarely)
            var knownIds = byId.Keys.ToHashSet(StringComparer.Ordinal);

            // Pass 2: call graph (callees within the analyzed set → callers) + referenced names for tagging.
            var calleesById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var refNamesById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var callersById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var m in byId.Values)
            {
                ct.ThrowIfCancellationRequested();
                var (callees, refNames) = AnalyzeBody(m, knownIds, ct);
                calleesById[m.Id] = callees;
                refNamesById[m.Id] = refNames;
                foreach (var callee in callees)
                    (callersById.TryGetValue(callee, out var set) ? set : callersById[callee] = new(StringComparer.Ordinal)).Add(m.Id);
            }

            // Pass 3: assemble the enriched IR.
            var units = new List<CodeUnit>(byId.Count);
            foreach (var m in byId.Values)
            {
                ct.ThrowIfCancellationRequested();
                units.Add(ToCodeUnit(
                    m,
                    calleesById[m.Id],
                    callersById.TryGetValue(m.Id, out var callers) ? callers : new HashSet<string>(),
                    refNamesById[m.Id],
                    testedIds));
            }

            _log?.Invoke($"[analyze] {units.Count} VB production member(s) found.");
            return units;
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

    /// <summary>One gathered VB member with the context the enrichment passes need.</summary>
    private sealed record RawVb(
        IMethodSymbol Symbol, SyntaxNode Decl, MethodBlockBaseSyntax? Body,
        SemanticModel Model, IReadOnlyList<string> Imports, string Id);

    /// <summary>Every behaviour-carrying member: Sub/Function, constructors (Sub New), user-defined
    /// operators/conversions, and explicit property Get/Set accessors (those with a body). Mirrors the C#
    /// adapter, which analyzes computed property accessors too — without this, logic in VB property getters
    /// (common in legacy WebForms domain models) would be silently omitted from the readiness report.</summary>
    private static IEnumerable<(IMethodSymbol Symbol, SyntaxNode Decl, MethodBlockBaseSyntax? Body)> BehaviorMembers(
        SyntaxNode root, SemanticModel model, CancellationToken ct)
    {
        foreach (var stmt in root.DescendantNodes().OfType<MethodBaseSyntax>())
        {
            // Auto-property accessors have no AccessorBlock (no AccessorStatementSyntax), so they never
            // appear here. Declare/Delegate/event statements and event add/remove/raise handlers are
            // filtered out by MethodKind below.
            if (stmt is not (MethodStatementSyntax or SubNewStatementSyntax or OperatorStatementSyntax or AccessorStatementSyntax)) continue;
            if (model.GetDeclaredSymbol(stmt, ct) is not IMethodSymbol symbol) continue;
            if (symbol.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor
                or MethodKind.UserDefinedOperator or MethodKind.Conversion
                or MethodKind.PropertyGet or MethodKind.PropertySet)) continue;
            yield return (symbol, stmt, stmt.Parent as MethodBlockBaseSyntax);
        }
    }

    /// <summary>Resolve calls/constructions in a member's body into in-graph callee ids (blast radius)
    /// plus a bag of referenced names that sharpen domain tagging.</summary>
    private static (HashSet<string> Callees, HashSet<string> RefNames) AnalyzeBody(
        RawVb m, HashSet<string> knownIds, CancellationToken ct)
    {
        var callees = new HashSet<string>(StringComparer.Ordinal);
        var refNames = new HashSet<string>(StringComparer.Ordinal);
        if (m.Body is null) return (callees, refNames);

        foreach (var node in m.Body.DescendantNodes())
        {
            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;
            if (m.Model.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol called) continue;

            refNames.Add(called.Name);
            if (called.ContainingType is { } type) refNames.Add(type.Name);

            string calleeId = VbSymbols.MethodId(called.OriginalDefinition);
            if (calleeId != m.Id && knownIds.Contains(calleeId)) callees.Add(calleeId);
        }
        return (callees, refNames);
    }

    /// <summary>Walk every VB test project's calls/constructions and record the production member ids
    /// they reference — symbol-level, so the "untested" count is a fact, not a guess.</summary>
    private static async Task<HashSet<string>> CollectTestedMethodIdsAsync(Solution solution, CancellationToken ct)
    {
        var tested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.VisualBasic) continue;

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null || !IsTestProject(project, compilation)) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                foreach (var node in root.DescendantNodes())
                {
                    if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;
                    if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol called)
                        tested.Add(VbSymbols.MethodId(called.OriginalDefinition));
                }
            }
        }
        return tested;
    }

    private static bool IsTestProject(Project project, Compilation compilation)
    {
        foreach (var asm in compilation.ReferencedAssemblyNames)
        {
            string name = asm.Name;
            if (name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("TestPlatform", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("MSTest", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return VbSymbols.LooksLikeTestName(project.Name);
    }

    private static CodeUnit ToCodeUnit(RawVb m, HashSet<string> callees, HashSet<string> callers, HashSet<string> refNames, HashSet<string> testedIds)
    {
        var symbol = m.Symbol;
        var span = m.Decl.GetLocation().GetLineSpan();
        int startLine = span.StartLinePosition.Line + 1;
        int endLine = ((SyntaxNode?)m.Body ?? m.Decl).GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var parameters = symbol.Parameters.Select(p => new ParamInfo(p.Name, VbSymbols.ShortType(p.Type))).ToList();

        // Domain tags: reuse the engine's language-neutral tagger (now fed referenced names too).
        var tags = DomainTagger.Tag(
            symbol.Name,
            symbol.ContainingType?.Name ?? "",
            parameters.Select(p => p.Type),
            symbol.ReturnsVoid ? "void" : VbSymbols.ShortType(symbol.ReturnType),
            refNames);

        // Landmines: same .NET vocabulary as C#, fed from VB syntax (Imports / Inherits / attributes / file).
        var typeBlock = m.Decl.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        var landmines = VbLandmineDetector.Detect(m.Imports, BaseTypeNames(typeBlock), AttributeNames(m.Decl, typeBlock), span.Path);

        return new CodeUnit(
            Id: m.Id,
            DisplayName: VbSymbols.DisplayName(symbol),
            FilePath: span.Path,
            StartLine: startLine,
            EndLine: endLine,
            Signature: VbSymbols.Signature(symbol),
            Parameters: parameters,
            ReturnType: symbol.ReturnsVoid ? "void" : VbSymbols.ShortType(symbol.ReturnType),
            CyclomaticComplexity: VbComplexity.Compute(m.Body),
            LineCount: Math.Max(1, endLine - startLine + 1),
            CallerIds: callers.ToList(),
            CalleeIds: callees.ToList(),
            DomainTags: tags,
            HasTests: testedIds.Contains(m.Id),
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
