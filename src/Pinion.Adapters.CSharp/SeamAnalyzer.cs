using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Feathers seam analysis: where can a unit be put under a characterization test, and where must a
/// seam be introduced first? Two complementary signals from the semantic model:
///
///  - <b>Seams available</b> — substitutable collaborators the unit already receives (constructor- or
///    parameter-injected interfaces, abstract classes, delegates). These are object seams you can use
///    today: pass a test double and the behaviour is observable.
///  - <b>Seam obstacles</b> — hard dependencies with no seam: non-deterministic/ambient access
///    (DateTime.Now, Guid.NewGuid, Environment) and external resources (File, Console, HttpClient,
///    SqlConnection, …) used directly. These resist testing AND are classic .NET Framework→Core
///    migration friction; Feathers' prescription is Extract Interface / Parameterize Constructor.
///
/// Deliberately conservative: it flags a curated set of well-known obstacles rather than guessing, so
/// the output stays explainable. Degrades gracefully — when a type doesn't resolve (legacy project that
/// won't fully restore) it simply isn't flagged, never crashes.
/// </summary>
internal static class SeamAnalyzer
{
    private const int MaxItems = 6;

    // Non-deterministic / ambient members — using these wires in time, identity, or environment.
    private static readonly Dictionary<string, HashSet<string>> AmbientMembers = new(StringComparer.Ordinal)
    {
        ["DateTime"] = new(StringComparer.Ordinal) { "Now", "UtcNow", "Today" },
        ["DateTimeOffset"] = new(StringComparer.Ordinal) { "Now", "UtcNow" },
        ["Guid"] = new(StringComparer.Ordinal) { "NewGuid" },
        ["Stopwatch"] = new(StringComparer.Ordinal) { "GetTimestamp", "StartNew" },
        // Machine/process-specific Environment access (NOT benign constants like NewLine/Version).
        ["Environment"] = new(StringComparer.Ordinal)
        {
            "GetEnvironmentVariable", "GetEnvironmentVariables", "ExpandEnvironmentVariables",
            "MachineName", "UserName", "UserDomainName", "CurrentDirectory", "GetCommandLineArgs",
            "CommandLine", "ProcessPath", "TickCount", "TickCount64",
        },
    };

    // External-resource types — any static call or construction is an I/O / network / DB dependency.
    private static readonly HashSet<string> ResourceTypes = new(StringComparer.Ordinal)
    {
        "File", "Directory", "FileInfo", "DirectoryInfo", "FileStream", "StreamReader", "StreamWriter",
        "Console", "HttpClient", "WebClient", "WebRequest", "HttpWebRequest", "Socket", "TcpClient",
        "SqlConnection", "SqlCommand", "OleDbConnection", "DbConnection", "Process", "Random",
    };

    // Interfaces that are data/utility contracts, not substitutable collaborators — never count as seams.
    private static readonly HashSet<string> NonCollaboratorInterfaces = new(StringComparer.Ordinal)
    {
        "IEnumerable", "IEnumerator", "ICollection", "IList", "IReadOnlyList", "IReadOnlyCollection",
        "IDictionary", "IReadOnlyDictionary", "ISet", "IComparable", "IEquatable", "IFormattable",
        "IConvertible", "ICloneable", "IDisposable", "IAsyncDisposable",
    };

    public static (IReadOnlyList<string> Seams, IReadOnlyList<string> Obstacles) Analyze(
        IMethodSymbol method, MethodDeclarationSyntax decl, SemanticModel model, CancellationToken ct)
    {
        var seams = new SortedSet<string>(StringComparer.Ordinal);
        var obstacles = new SortedSet<string>(StringComparer.Ordinal);

        // Object seams via injection: substitutable collaborators the unit already receives. Method
        // parameters apply to static and instance methods; constructor-injected collaborators only to
        // instance methods (a static method can't reach them).
        foreach (var p in method.Parameters)
            if (IsSubstitutable(p.Type))
                seams.Add(RoslynSymbols.ShortType(p.Type));

        if (!method.IsStatic)
            foreach (var ctor in method.ContainingType.InstanceConstructors)
                if (ctor.DeclaredAccessibility != Accessibility.Private)
                    foreach (var p in ctor.Parameters)
                        if (IsSubstitutable(p.Type))
                            seams.Add(RoslynSymbols.ShortType(p.Type));

        // Obstacles: ambient access and external-resource use inside the body.
        SyntaxNode? body = (SyntaxNode?)decl.Body ?? decl.ExpressionBody;
        if (body is not null)
        {
            foreach (var node in body.DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();
                switch (node)
                {
                    case ObjectCreationExpressionSyntax oc
                        when model.GetSymbolInfo(oc, ct).Symbol is IMethodSymbol { ContainingType.Name: { } tn }
                             && ResourceTypes.Contains(tn):
                        obstacles.Add("new " + tn);
                        break;

                    case InvocationExpressionSyntax inv
                        when model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol called:
                        AddCallObstacle(obstacles, called.ContainingType?.Name, called.Name);
                        break;

                    // DateTime.Now / Environment.MachineName etc. are property reads, not invocations.
                    case MemberAccessExpressionSyntax ma
                        when model.GetSymbolInfo(ma, ct).Symbol is IPropertySymbol prop:
                        AddCallObstacle(obstacles, prop.ContainingType?.Name, prop.Name);
                        break;
                }
            }
        }

        return (Cap(seams), Cap(obstacles));
    }

    private static void AddCallObstacle(SortedSet<string> obstacles, string? typeName, string member)
    {
        if (typeName is null) return;
        if (AmbientMembers.TryGetValue(typeName, out var members) && members.Contains(member))
            obstacles.Add($"{typeName}.{member}");
        else if (ResourceTypes.Contains(typeName))
            obstacles.Add(typeName);
    }

    private static bool IsSubstitutable(ITypeSymbol t) => t.TypeKind switch
    {
        TypeKind.Delegate => true,
        TypeKind.Interface => !NonCollaboratorInterfaces.Contains(t.Name),
        TypeKind.Class => t.IsAbstract,
        _ => false,
    };

    private static IReadOnlyList<string> Cap(SortedSet<string> set) => set.Take(MaxItems).ToList();
}
