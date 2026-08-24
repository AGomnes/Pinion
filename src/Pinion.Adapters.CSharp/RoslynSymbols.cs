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
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
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

    public static string MethodId(IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.LocalFunction && method.ContainingSymbol is IMethodSymbol outer)
        {
            string pars = string.Join(", ", method.Parameters.Select(p => RefPrefix(p.RefKind) + ShortType(p.Type)));
            return $"{MethodId(outer)}/{method.Name}({pars})";
        }
        return method.OriginalDefinition.ToDisplayString(IdFormat);
    }

    private static string RefPrefix(RefKind kind) => kind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        _ => "",
    };

    public static string TypeId(INamedTypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString(IdFormat);

    public static string ShortType(ITypeSymbol type) => type.ToDisplayString(ShortTypeFormat);

    /// <summary>"InvoiceService.CalculateVat", "Cart.ctor", "Order.Total.get" — friendly enough for report rows.</summary>
    public static string DisplayName(IMethodSymbol method)
    {
        string typeName = method.ContainingType?.Name ?? "";
        string name = MemberLabel(method);
        return string.IsNullOrEmpty(typeName) ? name : $"{typeName}.{name}";
    }

    private static string MemberLabel(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.Constructor or MethodKind.StaticConstructor => "ctor",
        MethodKind.Destructor => "~dtor",
        MethodKind.PropertyGet => AssociatedName(method) + ".get",
        MethodKind.PropertySet => AssociatedName(method) + (method.IsInitOnly ? ".init" : ".set"),
        MethodKind.EventAdd => AssociatedName(method) + ".add",
        MethodKind.EventRemove => AssociatedName(method) + ".remove",
        MethodKind.LocalFunction => method.Name + " (local)",
        _ => method.Name,
    };

    private static string AssociatedName(IMethodSymbol accessor) => accessor.AssociatedSymbol?.Name ?? accessor.Name;

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
