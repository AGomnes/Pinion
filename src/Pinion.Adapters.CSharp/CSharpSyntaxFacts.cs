using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pinion.Adapters.CSharp;

/// <summary>Cheap, resolution-free extraction of the syntactic signals the enrichers need.</summary>
internal static class CSharpSyntaxFacts
{
    /// <summary>Every using namespace in a file (top-level, namespace-scoped, and static).</summary>
    public static IReadOnlyList<string> FileUsings(SyntaxNode root) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Simple base-type/interface names declared on the type, e.g. "Page", "DbContext".</summary>
    public static IReadOnlyList<string> BaseTypeNames(TypeDeclarationSyntax? type) =>
        type?.BaseList?.Types
            .Select(t => LastIdentifier(t.Type.ToString()))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList()
        ?? new List<string>();

    /// <summary>Simple attribute names on the type and the member, e.g. "ServiceContract". Accepts any
    /// member-declaration node (method, constructor, operator, property/indexer, accessor, local function).</summary>
    public static IReadOnlyList<string> AttributeNames(SyntaxNode member, TypeDeclarationSyntax? type)
    {
        IEnumerable<AttributeListSyntax> lists = member switch
        {
            MemberDeclarationSyntax md => md.AttributeLists,          // method/ctor/operator/property/indexer
            AccessorDeclarationSyntax a => a.AttributeLists,          // get/set/init/add/remove
            LocalFunctionStatementSyntax lf => lf.AttributeLists,     // local function
            _ => Enumerable.Empty<AttributeListSyntax>(),
        };
        if (type is not null) lists = lists.Concat(type.AttributeLists);

        return lists
            .SelectMany(l => l.Attributes)
            .Select(a => LastIdentifier(a.Name.ToString()))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>"System.Web.UI.Page" → "Page"; "List&lt;T&gt;" → "List".</summary>
    private static string LastIdentifier(string typeText)
    {
        int generic = typeText.IndexOf('<');
        if (generic >= 0) typeText = typeText[..generic];
        int dot = typeText.LastIndexOf('.');
        return (dot >= 0 ? typeText[(dot + 1)..] : typeText).Trim();
    }
}
