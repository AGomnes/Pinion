using Pinion.Engine.Model;

namespace Pinion.Adapters.VisualBasic;

/// <summary>
/// VB.NET migration-landmine detection — WebForms / WCF / EF6. Identical logic and .NET-namespace
/// vocabulary as the C# LandmineDetector (VB shares the framework namespaces); only the WebForms
/// code-behind file suffixes differ (.aspx.vb / .ascx.vb). Mirrors the C# detector; promote to a
/// shared engine component when VB graduates from spike.
/// </summary>
internal static class VbLandmineDetector
{
    public static IReadOnlyList<string> Detect(
        IReadOnlyCollection<string> imports,
        IReadOnlyCollection<string> baseTypeNames,
        IReadOnlyCollection<string> attributeNames,
        string filePath)
    {
        var found = new List<string>();

        bool wcf =
            Has(imports, "System.ServiceModel")
            || AnyName(attributeNames, "ServiceContract", "OperationContract", "ServiceBehavior");
        if (wcf) found.Add(MigrationLandmine.Wcf);

        bool webForms =
            Has(imports, "System.Web.UI")
            || AnyName(baseTypeNames, "Page", "UserControl", "MasterPage")
            || EndsWithAny(filePath, ".aspx.vb", ".ascx.vb", ".master.vb");
        if (webForms) found.Add(MigrationLandmine.WebForms);

        bool efCore = Has(imports, "Microsoft.EntityFrameworkCore");
        bool ef6 =
            Has(imports, "System.Data.Entity")
            || AnyName(baseTypeNames, "ObjectContext")
            || (AnyName(baseTypeNames, "DbContext", "DbConfiguration") && !efCore);
        if (ef6) found.Add(MigrationLandmine.Ef6);

        return found;
    }

    private static bool Has(IReadOnlyCollection<string> imports, string ns) =>
        imports.Any(u => u.Equals(ns, StringComparison.Ordinal) || u.StartsWith(ns + ".", StringComparison.Ordinal));

    private static bool AnyName(IReadOnlyCollection<string> names, params string[] wanted) =>
        names.Any(n => wanted.Any(w =>
            n.Equals(w, StringComparison.Ordinal) || n.Equals(w + "Attribute", StringComparison.Ordinal)));

    private static bool EndsWithAny(string path, params string[] suffixes) =>
        suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}
