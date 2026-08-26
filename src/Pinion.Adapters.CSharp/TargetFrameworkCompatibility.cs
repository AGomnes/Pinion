using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pinion.Engine.Analysis;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Answers "what does this code use that will not exist on .NET N" by resolving every framework type
/// the code references against the TARGET framework's own reference assemblies.
///
/// No curated catalog of removed APIs: the reference pack is the ground truth, ships with the SDK, is
/// exact for the version being targeted, and cannot go stale. A hand-maintained list would be wrong the
/// day the next .NET shipped.
///
/// Deliberately conservative. Only types that resolve to a *framework* assembly in the source
/// compilation are checked, so a missing NuGet package or an unresolvable legacy reference is reported
/// as nothing rather than as a false migration blocker. Legacy projects frequently do not fully restore
/// on a modern machine, and a readiness report that cries wolf is worse than one that says less.
/// </summary>
internal static class TargetFrameworkCompatibility
{
    /// <summary>Assembly name prefixes treated as "the framework". Anything else is the user's own code
    /// or a third-party package, whose availability is a packaging question, not an API-removal one.</summary>
    private static readonly string[] FrameworkAssemblyPrefixes =
    {
        "System", "mscorlib", "netstandard", "Microsoft.Win32", "Microsoft.VisualBasic", "WindowsBase",
        "PresentationCore", "PresentationFramework", "Accessibility",
    };

    /// <summary>
    /// Types used by <paramref name="compilation"/> that are absent from <paramref name="targetTfm"/>.
    /// Returns empty when the target's reference assemblies are not installed — an unknown answer must
    /// never be reported as "incompatible".
    /// </summary>
    public static IReadOnlyList<IncompatibleApi> Check(
        Compilation compilation, string targetTfm, Action<string>? log, CancellationToken ct)
    {
        string? refDir = ReferencePackLocator.Locate(targetTfm);
        if (refDir is null)
        {
            log?.Invoke($"[compat] no reference assemblies installed for {targetTfm}; skipping compatibility check.");
            return Array.Empty<IncompatibleApi>();
        }

        var probe = BuildProbe(refDir);
        if (probe is null)
        {
            log?.Invoke($"[compat] could not load reference assemblies from {refDir}; skipping.");
            return Array.Empty<IncompatibleApi>();
        }

        var hits = new Dictionary<string, (string Assembly, int Count, string File, int Line)>(StringComparer.Ordinal);
        var verdictCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);

            foreach (var node in root.DescendantNodes())
            {
                if (node is not (Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax
                    or Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax))
                    continue;

                if (model.GetSymbolInfo(node, ct).Symbol is not INamedTypeSymbol type) continue;
                if (NamedTypeOf(type) is not { } named) continue;
                if (!IsFrameworkType(named, out string assembly)) continue;

                string metadataName = FullMetadataName(named);
                if (metadataName.Length == 0) continue;

                if (!verdictCache.TryGetValue(metadataName, out bool missing))
                {
                    missing = probe.GetTypeByMetadataName(metadataName) is null;
                    verdictCache[metadataName] = missing;
                }
                if (!missing) continue;

                var pos = tree.GetLineSpan(node.Span).StartLinePosition;
                if (hits.TryGetValue(metadataName, out var prev))
                    hits[metadataName] = prev with { Count = prev.Count + 1 };
                else
                    hits[metadataName] = (assembly, 1, tree.FilePath, pos.Line + 1);
            }
        }

        return hits
            .Select(kv => new IncompatibleApi(kv.Key, kv.Value.Assembly, kv.Value.Count, kv.Value.File, kv.Value.Line))
            .OrderByDescending(a => a.UsageCount)
            .ThenBy(a => a.TypeName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A compilation containing only the target framework's reference assemblies, used purely
    /// to ask "does this type exist there".</summary>
    private static Compilation? BuildProbe(string refDir)
    {
        var refs = new List<MetadataReference>();
        foreach (string dll in Directory.GetFiles(refDir, "*.dll"))
        {
            try { refs.Add(MetadataReference.CreateFromFile(dll)); }
            catch { /* a malformed or locked ref assembly must not fail the whole audit */ }
        }
        return refs.Count == 0 ? null : CSharpCompilation.Create("PinionTargetProbe", references: refs);
    }

    /// <summary>Unwrap generics/arrays/nullables to the underlying named type worth checking.</summary>
    private static INamedTypeSymbol? NamedTypeOf(INamedTypeSymbol type)
    {
        var t = type.OriginalDefinition;
        return t.TypeKind is TypeKind.Error or TypeKind.TypeParameter ? null : t;
    }

    private static bool IsFrameworkType(INamedTypeSymbol type, out string assembly)
    {
        string name = type.ContainingAssembly?.Name ?? "";
        assembly = name;
        if (name.Length == 0) return false;
        return FrameworkAssemblyPrefixes.Any(p =>
            name.Equals(p, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Metadata name including namespace and arity, the form <c>GetTypeByMetadataName</c> wants
    /// (e.g. <c>System.Collections.Generic.List`1</c>, nested as <c>Outer+Inner</c>).</summary>
    private static string FullMetadataName(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            parts.Push(t.MetadataName);

        string nested = string.Join("+", parts);
        string ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + nested;
    }
}
