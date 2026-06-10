using Microsoft.CodeAnalysis;

namespace Pinion.Adapters.VisualBasic;

/// <summary>
/// Stable symbol identifiers and accessibility helpers — the symbol layer is language-neutral, so these
/// mirror the C# adapter's RoslynSymbols. (When VB graduates from spike, promote the shared symbol
/// formatting to a common adapter base rather than duplicating.)
/// </summary>
internal static class VbSymbols
{
    private static readonly SymbolDisplayFormat IdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat ShortTypeFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeAccessibility,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string MethodId(IMethodSymbol method) => method.OriginalDefinition.ToDisplayString(IdFormat);

    public static string ShortType(ITypeSymbol type) => type.ToDisplayString(ShortTypeFormat);

    public static string Signature(IMethodSymbol method) => method.ToDisplayString(SignatureFormat);

    public static string DisplayName(IMethodSymbol method)
    {
        string typeName = method.ContainingType?.Name ?? "";
        string name = method.MethodKind == MethodKind.Constructor ? "ctor" : method.Name;
        return string.IsNullOrEmpty(typeName) ? name : $"{typeName}.{name}";
    }

    /// <summary>Reachable from outside the assembly — its behaviour is a contract.</summary>
    public static bool IsPublicEntryPoint(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
            return false;
        for (INamedTypeSymbol? t = method.ContainingType; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        return true;
    }
}
