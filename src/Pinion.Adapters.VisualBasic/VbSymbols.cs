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
        string name = MemberLabel(method);
        return string.IsNullOrEmpty(typeName) ? name : $"{typeName}.{name}";
    }

    // "InvoiceService.CalculateVat", "Cart.ctor", "Order.Total.get" — friendly report-row labels.
    // Mirrors the C# adapter's RoslynSymbols.MemberLabel so VB property/operator members read the same.
    private static string MemberLabel(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => "ctor",
        MethodKind.PropertyGet => AssociatedName(method) + ".get",
        MethodKind.PropertySet => AssociatedName(method) + ".set",
        _ => method.Name, // ordinary Sub/Function, user-defined operators, conversions
    };

    private static string AssociatedName(IMethodSymbol accessor) => accessor.AssociatedSymbol?.Name ?? accessor.Name;

    /// <summary>Name-based test heuristic — the fallback when a project doesn't reference a known test
    /// framework. Deliberately plural/dotted ("…Tests", "….Test") so production names like "SmokeTest"
    /// or "MyTest" aren't misclassified as tests (which would hide their code from the readiness report).</summary>
    public static bool LooksLikeTestName(string name) =>
        name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Test", StringComparison.OrdinalIgnoreCase);

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
