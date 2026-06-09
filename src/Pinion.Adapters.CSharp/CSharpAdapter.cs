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
    private readonly bool _includeReferencedProjects;

    public CSharpAdapter(Action<string>? log = null, bool includeReferencedProjects = false)
    {
        _log = log;
        _includeReferencedProjects = includeReferencedProjects;
    }

    public string Language => "csharp";

    /// <summary>One production member (method, ctor, operator, accessor, or local function), with the
    /// context the enrichment passes need. <see cref="Body"/> is the block/expression body, or null for a
    /// bodyless member (abstract/extern/partial declaration).</summary>
    private sealed record RawMethod(
        IMethodSymbol Symbol,
        SyntaxNode Decl,
        SyntaxNode? Body,
        SemanticModel Model,
        string Id,
        IReadOnlyList<string> Usings);

    public async Task<IReadOnlyList<CodeUnit>> AnalyzeAsync(string projectOrSolutionPath, CancellationToken ct)
    {
        string path = ResolveInputPath(projectOrSolutionPath);

        // `analyze Foo.csproj` should report Foo's methods — not silently include methods from Foo's
        // project references. So scope to the target project unless the input is a solution or the caller
        // opted into referenced projects.
        bool isProject = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        string? targetProjectPath = isProject && !_includeReferencedProjects ? path : null;

        MSBuildWorkspace? msbuild = null;
        Workspace? scanWorkspace = null;
        try
        {
            Solution solution = await LoadSolutionAsync(path, ct, w => msbuild = w).ConfigureAwait(false);
            // The source-scan fallback returns an AdhocWorkspace that isn't `msbuild`; track it so it's
            // disposed too (it was previously leaked for the process lifetime).
            if (!ReferenceEquals(solution.Workspace, msbuild)) scanWorkspace = solution.Workspace;
            return await AnalyzeSolutionAsync(solution, targetProjectPath, ct).ConfigureAwait(false);
        }
        finally
        {
            msbuild?.Dispose();
            scanWorkspace?.Dispose();
        }
    }

    private async Task<IReadOnlyList<CodeUnit>> AnalyzeSolutionAsync(Solution solution, string? targetProjectPath, CancellationToken ct)
    {
        // Which symbols any test project references — turns "untested" into a fact.
        var testedMethodIds = await CollectTestedMethodIdsAsync(solution, ct).ConfigureAwait(false);

        // Pass 1: gather every production method with its declaration + semantic model.
        var raw = await GatherProductionMethodsAsync(solution, targetProjectPath, ct).ConfigureAwait(false);
        var byId = new Dictionary<string, RawMethod>(StringComparer.Ordinal);
        foreach (var m in raw) byId.TryAdd(m.Id, m); // first declaration wins (partials/dupes)
        var knownIds = byId.Keys.ToHashSet(StringComparer.Ordinal);

        // Syntactic (simple name, parameter count) → ids index, used to recover calls Roslyn can't resolve
        // on unrestored/legacy code (where blast-radius otherwise collapses to ~0).
        var nameArity = new Dictionary<(string, int), List<string>>();
        foreach (var m in byId.Values)
        {
            var key = (m.Symbol.Name, m.Symbol.Parameters.Length);
            (nameArity.TryGetValue(key, out var list) ? list : nameArity[key] = new()).Add(m.Id);
        }

        // Pass 2: call graph (callees within the analyzed set) + referenced names for tagging.
        var calleesById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var refNamesById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var callersById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var m in byId.Values)
        {
            ct.ThrowIfCancellationRequested();
            var (callees, refNames) = AnalyzeBody(m, knownIds, nameArity, ct);
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

    private async Task<List<RawMethod>> GatherProductionMethodsAsync(Solution solution, string? targetProjectPath, CancellationToken ct)
    {
        var raw = new List<RawMethod>();
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp) continue;
            // Scope to the target project when requested (single-project analyze without --include-refs).
            // Only filter projects that HAVE a file path — the source-scan fallback flattens everything into
            // one ad-hoc project with no path, and that IS the target, so it must never be filtered out.
            if (targetProjectPath is not null && project.FilePath is not null && !SamePath(project.FilePath, targetProjectPath)) continue;

            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null || IsTestProject(project, compilation)) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                var usings = CSharpSyntaxFacts.FileUsings(root);

                foreach (var (symbol, decl, body) in BehaviorMembers(root, model, ct))
                    raw.Add(new RawMethod(symbol, decl, body, model, RoslynSymbols.MethodId(symbol), usings));
            }
        }

        _log?.Invoke($"[analyze] {raw.Count} production member(s) found.");
        return raw;
    }

    /// <summary>
    /// Every behaviour-carrying member in a file — not just methods. Covers methods, constructors,
    /// operators/conversions, local functions, and computed property/indexer accessors (those with a
    /// body). Auto-property accessors carry no behaviour and are skipped; bodyless METHODS are kept as
    /// complexity-1 units for parity with prior behaviour.
    /// </summary>
    internal static IEnumerable<(IMethodSymbol Symbol, SyntaxNode Decl, SyntaxNode? Body)> BehaviorMembers(
        SyntaxNode root, SemanticModel model, CancellationToken ct)
    {
        foreach (var node in root.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            switch (node)
            {
                // Method, constructor, operator, conversion operator, destructor.
                case BaseMethodDeclarationSyntax bm:
                    if (model.GetDeclaredSymbol(bm, ct) is IMethodSymbol ms)
                        yield return (ms, bm, BodyOf(bm));
                    break;

                case LocalFunctionStatementSyntax lf when BodyOf(lf) is { } lb:
                    if (model.GetDeclaredSymbol(lf, ct) is IMethodSymbol ls)
                        yield return (ls, lf, lb);
                    break;

                // get/set/init/add/remove with a real body (computed accessor) — skips auto-properties.
                case AccessorDeclarationSyntax a when BodyOf(a) is { } ab:
                    if (model.GetDeclaredSymbol(a, ct) is IMethodSymbol asym)
                        yield return (asym, a, ab);
                    break;

                // Expression-bodied property / indexer: `public int X => …;` (its getter is the unit).
                case PropertyDeclarationSyntax p when p.ExpressionBody is { } pe:
                    if (model.GetDeclaredSymbol(p, ct) is IPropertySymbol { GetMethod: { } pg })
                        yield return (pg, p, pe);
                    break;

                case IndexerDeclarationSyntax ix when ix.ExpressionBody is { } ie:
                    if (model.GetDeclaredSymbol(ix, ct) is IPropertySymbol { GetMethod: { } ig })
                        yield return (ig, ix, ie);
                    break;
            }
        }
    }

    /// <summary>The block or expression body of any member-like declaration, or null if it has none.</summary>
    internal static SyntaxNode? BodyOf(SyntaxNode decl) => decl switch
    {
        BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
        LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
        PropertyDeclarationSyntax p => p.ExpressionBody,
        IndexerDeclarationSyntax ix => ix.ExpressionBody,
        _ => null,
    };

    /// <summary>Resolve calls in a method body into in-graph callee ids and a bag of referenced names.</summary>
    private static (HashSet<string> Callees, HashSet<string> RefNames) AnalyzeBody(
        RawMethod m, HashSet<string> knownIds,
        IReadOnlyDictionary<(string, int), List<string>> nameArity, CancellationToken ct)
    {
        var callees = new HashSet<string>(StringComparer.Ordinal);
        var refNames = new HashSet<string>(StringComparer.Ordinal);

        SyntaxNode? body = m.Body;
        if (body is null) return (callees, refNames);

        foreach (var node in body.DescendantNodes())
        {
            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;

            var info = m.Model.GetSymbolInfo(node, ct);
            // Prefer the resolved symbol; on unrestored/legacy code, fall back to the overload candidates
            // Roslyn knew but couldn't pick (CandidateSymbols) so the call graph stays populated.
            IMethodSymbol? called = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            if (called is not null)
            {
                refNames.Add(called.Name);
                if (called.ContainingType is { } ct2) refNames.Add(ct2.Name);

                string calleeId = RoslynSymbols.MethodId(called.OriginalDefinition);
                if (calleeId != m.Id && knownIds.Contains(calleeId)) callees.Add(calleeId);
                continue;
            }

            // Fully unresolved (typical when references didn't restore): recover blast-radius with a
            // SYNTACTIC name+arity match, but only when UNAMBIGUOUS (exactly one in-set method) so a common
            // name like "Add" can't be misattributed across overloads/types.
            if (node is InvocationExpressionSyntax inv && InvokedName(inv) is { } name)
            {
                refNames.Add(name);
                if (UniqueCalleeByName(nameArity, name, inv.ArgumentList.Arguments.Count, m.Id) is { } id)
                    callees.Add(id);
            }
        }

        return (callees, refNames);
    }

    /// <summary>The single in-set method id matching (name, arity), or null when there is none or more than
    /// one (ambiguous — refuse to guess, so a common name can't be misattributed) or it would be self.</summary>
    internal static string? UniqueCalleeByName(
        IReadOnlyDictionary<(string, int), List<string>> nameArity, string name, int arity, string selfId) =>
        nameArity.TryGetValue((name, arity), out var ids) && ids.Count == 1 && ids[0] != selfId ? ids[0] : null;

    /// <summary>The simple method name invoked, syntactically — for the resolution-free blast-radius fallback.</summary>
    private static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,   // x.Foo()
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,  // x?.Foo()
        IdentifierNameSyntax id => id.Identifier.ValueText,                // Foo()
        _ => null,
    };

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
        SyntaxNode? body = m.Body;

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

        var (seams, seamObstacles) = SeamAnalyzer.Analyze(symbol, body, m.Model, ct);

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
                    switch (node)
                    {
                        // A call or construction protects the method/constructor it targets.
                        case InvocationExpressionSyntax:
                        case ObjectCreationExpressionSyntax:
                            if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol method)
                                tested.Add(RoslynSymbols.MethodId(method));
                            break;

                        // Reading/writing a property or indexer in a test protects its accessor(s) — needed
                        // now that computed properties/indexers are their own units (else they'd look untested).
                        case MemberAccessExpressionSyntax:
                        case ElementAccessExpressionSyntax:
                            if (model.GetSymbolInfo(node, ct).Symbol is IPropertySymbol prop)
                            {
                                if (prop.GetMethod is { } getter) tested.Add(RoslynSymbols.MethodId(getter));
                                if (prop.SetMethod is { } setter) tested.Add(RoslynSymbols.MethodId(setter));
                            }
                            break;
                    }
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

    /// <summary>Case-insensitive full-path equality (both non-null).</summary>
    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
