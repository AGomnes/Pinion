using Pinion.Adapters.CSharp;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class LandmineDetectorTests
{
    private static IReadOnlyList<string> Detect(
        string[]? usings = null, string[]? bases = null, string[]? attrs = null, string file = "X.cs") =>
        LandmineDetector.Detect(
            usings ?? Array.Empty<string>(),
            bases ?? Array.Empty<string>(),
            attrs ?? Array.Empty<string>(),
            file);

    [Fact]
    public void Wcf_detected_via_using_or_attribute()
    {
        Assert.Contains(MigrationLandmine.Wcf, Detect(usings: new[] { "System.ServiceModel" }));
        Assert.Contains(MigrationLandmine.Wcf, Detect(attrs: new[] { "ServiceContract" }));
    }

    [Fact]
    public void WebForms_detected_via_using_base_or_filename()
    {
        Assert.Contains(MigrationLandmine.WebForms, Detect(usings: new[] { "System.Web.UI" }));
        Assert.Contains(MigrationLandmine.WebForms, Detect(bases: new[] { "Page" }));
        Assert.Contains(MigrationLandmine.WebForms, Detect(file: "Report.aspx.cs"));
    }

    [Fact]
    public void Ef6_detected_via_old_namespace_but_not_efcore_dbcontext()
    {
        Assert.Contains(MigrationLandmine.Ef6, Detect(usings: new[] { "System.Data.Entity" }));
        Assert.Contains(MigrationLandmine.Ef6, Detect(bases: new[] { "ObjectContext" }));

        // A DbContext WITH the EF Core namespace is EF Core — don't flag it.
        Assert.DoesNotContain(MigrationLandmine.Ef6,
            Detect(usings: new[] { "Microsoft.EntityFrameworkCore" }, bases: new[] { "DbContext" }));
    }

    [Fact]
    public void Ef6_detected_for_a_derived_dbcontext_with_no_efcore_namespace()
    {
        // Previously a dead branch: a custom-named context `: DbContext` whose file imports neither the
        // EF6 namespace nor EF Core (common in legacy code split across files) is now flagged EF6.
        Assert.Contains(MigrationLandmine.Ef6, Detect(bases: new[] { "DbContext" }));
        Assert.Contains(MigrationLandmine.Ef6, Detect(bases: new[] { "DbConfiguration" }));
    }

    [Fact]
    public void Clean_code_has_no_landmines()
    {
        Assert.Empty(Detect(usings: new[] { "System", "System.Linq" }));
    }
}
