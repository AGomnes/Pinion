using Microsoft.CodeAnalysis;

namespace Pinion.Adapters.CSharp;

/// <summary>Stable, cross-compilation symbol identifiers and accessibility helpers.</summary>
internal static class RoslynSymbols
{
    /// <summary>
    /// Format that yields ids like "MyNs.InvoiceService.CalculateVat(decimal, int)".
    /// Source-derived, so the same logical method has the same id whether it is seen
    /// from the production compilation or referenced from a test compilation.
    /// </summary>
    private static readonly SymbolDisplayFormat IdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Type-minimal display, e.g. "decimal", "List&lt;string&gt;".</summary>
    private static readonly SymbolDisplayFormat ShortTypeFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Readable signature, e.g. "public decimal CalculateVat(decimal amount, int rate)".</summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeAccessibility,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string Signature(IMethodSymbol method) => method.ToDisplayString(SignatureFormat);

    public static string MethodId(IMethodSymbol method) =>
        method.OriginalDefinition.ToDisplayString(IdFormat);

    public static string TypeId(INamedTypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString(IdFormat);

    public static string ShortType(ITypeSymbol type) => type.ToDisplayString(ShortTypeFormat);

    /// <summary>"InvoiceService.CalculateVat" — friendly enough for report rows.</summary>
    public static string DisplayName(IMethodSymbol method)
    {
        string typeName = method.ContainingType?.Name ?? "";
        string name = method.MethodKind == MethodKind.Constructor ? "ctor" : method.Name;
        return string.IsNullOrEmpty(typeName) ? name : $"{typeName}.{name}";
    }

    /// <summary>
    /// A method is a public entry point when it (and the type that exposes it) are
    /// reachable from outside the assembly — its behavior is a contract.
    /// </summary>
    public static bool IsPublicEntryPoint(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
            return false;

        for (INamedTypeSymbol? t = method.ContainingType; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }
        return true;
    }
}
