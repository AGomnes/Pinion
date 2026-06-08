using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Pinion.Engine.Abstractions;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// The C#/.NET language adapter. It is the only place in the product that touches
/// Roslyn; everything it hands back is the language-neutral IR.
/// </summary>
/// <remarks>
/// MSBuild must be registered (via <c>MSBuildLocator.RegisterDefaults()</c>) by the
/// host process before the first call to <see cref="AnalyzeAsync"/>.
/// </remarks>
public sealed class CSharpAdapter : ILanguageAdapter
{
    private readonly Action<string>? _log;

    public CSharpAdapter(Action<string>? log = null) => _log = log;

    public string Language => "csharp";

    /// <summary>One production method, with the context the enrichment passes need.</summary>
    private sealed record RawMethod(
        IMethodSymbol Symbol,
        MethodDeclarationSyntax Decl,
        SemanticModel Model,
        string Id,
        IReadOnlyList<string> Usings);

    public async Task<IReadOnlyList<CodeUnit>> AnalyzeAsync(string projectOrSolutionPath, CancellationToken ct)
    {
        string path = ResolveInputPath(projectOrSolutionPath);

        MSBuildWorkspace? msbuild = null;
        try
        {
            Solution solution = await LoadSolutionAsync(path, ct, w => msbuild = w).ConfigureAwait(false);
            return await AnalyzeSolutionAsync(solution, ct).ConfigureAwait(false);
        }
        finally
        {
            msbuild?.Dispose();
        }
    }

    private async Task<IReadOnlyList<CodeUnit>> AnalyzeSolutionAsync(Solution solution, CancellationToken ct)
    {
        // Which symbols any test project references — turns "untested" into a fact.
        var testedMethodIds = await CollectTestedMethodIdsAsync(solution, ct).ConfigureAwait(false);

        // Pass 1: gather every production method with its declaration + semantic model.
        var raw = await GatherProductionMethodsAsync(solution, ct).ConfigureAwait(false);
        var byId = new Dictionary<string, RawMethod>(StringComparer.Ordinal);
        foreach (var m in raw) byId.TryAdd(m.Id, m); // first declaration wins (partials/dupes)
        var knownIds = byId.Keys.ToHashSet(StringComparer.Ordinal);

        // Pass 2: call graph (callees within the analyzed set) + referenced names for tagging.
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
                (callersById.TryGetValue(callee, out var set) ? set : callersById[callee] = new(StringComparer.Ordinal))
                    .Add(m.Id);
        }

        // Pass 3: assemble enriched IR.
        var units = new List<CodeUnit>(byId.Count);
        foreach (var m in byId.Values)
        {
            ct.ThrowIfCancellationRequested();
            units.Add(ToCodeUnit(
                m,
                testedMethodIds,
                calleesById[m.Id],
                callersById.TryGetValue(m.Id, out var callers) ? callers : new HashSet<string>(),
                refNamesById[m.Id],
                ct));
        }

        return units;
    }

    private async Task<List<RawMethod>> GatherProductionMethodsAsync(Solution solution, CancellationToken ct)
    {
        var raw = new List<RawMethod>();
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp) continue;

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null || IsTestProject(project, compilation)) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                var usings = CSharpSyntaxFacts.FileUsings(root);

                foreach (var decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(decl, ct) is not IMethodSymbol symbol) continue;
                    raw.Add(new RawMethod(symbol, decl, model, RoslynSymbols.MethodId(symbol), usings));
                }
            }
        }

        _log?.Invoke($"[analyze] {raw.Count} production method(s) found.");
        return raw;
    }

    /// <summary>Resolve calls in a method body into in-graph callee ids and a bag of referenced names.</summary>
    private static (HashSet<string> Callees, HashSet<string> RefNames) AnalyzeBody(
        RawMethod m, HashSet<string> knownIds, CancellationToken ct)
    {
        var callees = new HashSet<string>(StringComparer.Ordinal);
        var refNames = new HashSet<string>(StringComparer.Ordinal);

        SyntaxNode? body = (SyntaxNode?)m.Decl.Body ?? m.Decl.ExpressionBody;
        if (body is null) return (callees, refNames);

        foreach (var node in body.DescendantNodes())
        {
            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;
            if (m.Model.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol called) continue;

            refNames.Add(called.Name);
            if (called.ContainingType is { } ct2) refNames.Add(ct2.Name);

            string calleeId = RoslynSymbols.MethodId(called.OriginalDefinition);
            if (calleeId != m.Id && knownIds.Contains(calleeId))
                callees.Add(calleeId);
        }

        return (callees, refNames);
    }

    private static CodeUnit ToCodeUnit(
        RawMethod m,
        HashSet<string> testedMethodIds,
        HashSet<string> callees,
        HashSet<string> callers,
        HashSet<string> refNames,
        CancellationToken ct)
    {
        var symbol = m.Symbol;
        var decl = m.Decl;
        var lineSpan = decl.GetLocation().GetLineSpan();
        int startLine = lineSpan.StartLinePosition.Line + 1;
        int endLine = lineSpan.EndLinePosition.Line + 1;
        SyntaxNode? body = (SyntaxNode?)decl.Body ?? decl.ExpressionBody;

        var parameters = symbol.Parameters
            .Select(p => new ParamInfo(p.Name, RoslynSymbols.ShortType(p.Type)))
            .ToList();

        // Parameter names are strong domain signals ("amount", "token"), so feed them in too.
        var nameSignals = new List<string>(refNames);
        nameSignals.AddRange(symbol.Parameters.Select(p => p.Name));

        var tags = DomainTagger.Tag(
            symbol.Name,
            symbol.ContainingType?.Name ?? "",
            parameters.Select(p => p.Type),
            symbol.ReturnsVoid ? "void" : RoslynSymbols.ShortType(symbol.ReturnType),
            nameSignals);

        var typeDecl = decl.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var landmines = LandmineDetector.Detect(
            m.Usings,
            CSharpSyntaxFacts.BaseTypeNames(typeDecl),
            CSharpSyntaxFacts.AttributeNames(decl, typeDecl),
            lineSpan.Path);

        var (seams, seamObstacles) = SeamAnalyzer.Analyze(symbol, decl, m.Model, ct);

        return new CodeUnit(
            Id: m.Id,
            DisplayName: RoslynSymbols.DisplayName(symbol),
            FilePath: lineSpan.Path,
            StartLine: startLine,
            EndLine: endLine,
            Signature: RoslynSymbols.Signature(symbol),
            Parameters: parameters,
            ReturnType: symbol.ReturnsVoid ? "void" : RoslynSymbols.ShortType(symbol.ReturnType),
            CyclomaticComplexity: CyclomaticComplexity.Compute(body),
            LineCount: endLine - startLine + 1,
            CallerIds: callers.ToList(),
            CalleeIds: callees.ToList(),
            DomainTags: tags,
            HasTests: testedMethodIds.Contains(m.Id),
            IsPublicEntryPoint: RoslynSymbols.IsPublicEntryPoint(symbol),
            MigrationLandmines: landmines,
            Seams: seams,
            SeamObstacles: seamObstacles);
    }

    /// <summary>
    /// Walk every test project's call sites and record the production methods they
    /// invoke or construct. Symbol-level (not type-level) so the "unprotected" count
    /// is honest: a helper that no test calls directly is reported as exposed.
    /// </summary>
    private async Task<HashSet<string>> CollectTestedMethodIdsAsync(Solution solution, CancellationToken ct)
    {
        var tested = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp) continue;

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

                    if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol method)
                        tested.Add(RoslynSymbols.MethodId(method));
                }
            }
        }

        _log?.Invoke($"[analyze] {tested.Count} distinct method(s) referenced by tests.");
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
                || name.IndexOf("MSTest", StringComparison.OrdinalIgnoreCase) >= 0
                || name.Equals("Microsoft.VisualStudio.TestTools.UnitTesting", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return project.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || project.Name.EndsWith("Test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Load via MSBuild; if that fails or yields nothing (broken/missing MSBuild, legacy
    /// non-SDK projects, unrestored references), fall back to scanning .cs files directly
    /// so `analyze` still produces a report.
    /// </summary>
    private async Task<Solution> LoadSolutionAsync(
        string path, CancellationToken ct, Action<MSBuildWorkspace> onWorkspaceCreated)
    {
        try
        {
            var workspace = MSBuildWorkspace.Create();
            onWorkspaceCreated(workspace);
            workspace.RegisterWorkspaceFailedHandler(
                e => _log?.Invoke($"[roslyn] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));

            Solution sol = await OpenViaMSBuildAsync(workspace, path, ct).ConfigureAwait(false);
            if (HasCSharpDocuments(sol)) return sol;

            _log?.Invoke("[analyze] MSBuild produced no C# documents; scanning source files instead.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[analyze] project load failed ({ex.GetType().Name}: {ex.Message}); scanning source files instead.");
        }

        return SourceScanLoader.Load(path, _log);
    }

    internal static async Task<Solution> OpenViaMSBuildAsync(MSBuildWorkspace workspace, string path, CancellationToken ct)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return await workspace.OpenSolutionAsync(path, cancellationToken: ct).ConfigureAwait(false);
        }

        var project = await workspace.OpenProjectAsync(path, cancellationToken: ct).ConfigureAwait(false);
        return project.Solution;
    }

    internal static bool HasCSharpDocuments(Solution solution) =>
        solution.Projects.Any(p => p.Language == LanguageNames.CSharp && p.Documents.Any());

    /// <summary>Accepts a .sln/.csproj path or a directory; finds the obvious target in a directory.</summary>
    internal static string ResolveInputPath(string input)
    {
        if (File.Exists(input)) return Path.GetFullPath(input);

        if (Directory.Exists(input))
        {
            string dir = Path.GetFullPath(input);
            var sln = Directory.GetFiles(dir, "*.sln").Concat(Directory.GetFiles(dir, "*.slnx")).FirstOrDefault();
            if (sln is not null) return sln;

            var csproj = Directory.GetFiles(dir, "*.csproj");
            if (csproj.Length == 1) return csproj[0];
            if (csproj.Length > 1)
                throw new InvalidOperationException(
                    $"Multiple .csproj files in '{dir}'. Point at a specific .csproj or a .sln.");

            throw new FileNotFoundException($"No .sln or .csproj found in '{dir}'.");
        }

        throw new FileNotFoundException($"Path not found: '{input}'.");
    }
}
