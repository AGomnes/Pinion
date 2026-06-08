using Pinion.Engine.Model;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Detects the .NET Framework→Core migration killers — WebForms, WCF, EF6 — that
/// cause most migration failures and are prime behavior-change risks.
/// Deliberately syntactic (usings, base-type names, attribute names, file name): real
/// legacy projects often won't fully restore on a modern box, so we must not depend on
/// symbol resolution to see these.
/// </summary>
internal static class LandmineDetector
{
    public static IReadOnlyList<string> Detect(
        IReadOnlyCollection<string> usings,
        IReadOnlyCollection<string> baseTypeNames,
        IReadOnlyCollection<string> attributeNames,
        string filePath)
    {
        var found = new List<string>();

        bool wcf =
            Has(usings, "System.ServiceModel")
            || AnyName(attributeNames, "ServiceContract", "OperationContract", "ServiceBehavior");
        if (wcf) found.Add(MigrationLandmine.Wcf);

        bool webForms =
            Has(usings, "System.Web.UI")
            || AnyName(baseTypeNames, "Page", "UserControl", "MasterPage")
            || EndsWithAny(filePath, ".aspx.cs", ".ascx.cs", ".master.cs");
        if (webForms) found.Add(MigrationLandmine.WebForms);

        // EF6 lives in System.Data.Entity; EF Core's DbContext lives in
        // Microsoft.EntityFrameworkCore. Only flag EF6 when the old namespace is in
        // play (or the unambiguously-old ObjectContext is the base).
        bool ef6 =
            Has(usings, "System.Data.Entity")
            || AnyName(baseTypeNames, "ObjectContext")
            || (AnyName(baseTypeNames, "DbContext") && Has(usings, "System.Data.Entity"));
        if (ef6) found.Add(MigrationLandmine.Ef6);

        return found;
    }

    private static bool Has(IReadOnlyCollection<string> usings, string ns) =>
        usings.Any(u => u.Equals(ns, StringComparison.Ordinal)
            || u.StartsWith(ns + ".", StringComparison.Ordinal));

    private static bool AnyName(IReadOnlyCollection<string> names, params string[] wanted) =>
        names.Any(n => wanted.Any(w =>
            n.Equals(w, StringComparison.Ordinal) || n.Equals(w + "Attribute", StringComparison.Ordinal)));

    private static bool EndsWithAny(string path, params string[] suffixes) =>
        suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}
