using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Pinion.Adapters.VisualBasic;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class VbComplexityTests
{
    private static int Complexity(string vbSource, string method)
    {
        var tree = VisualBasicSyntaxTree.ParseText(vbSource);
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        _ = VisualBasicCompilation.Create("T", new[] { tree }, refs); // parse is enough; no symbols needed
        var block = tree.GetRoot().DescendantNodes().OfType<MethodBlockSyntax>()
            .First(b => b.SubOrFunctionStatement.Identifier.Text == method);
        return VbComplexity.Compute(block);
    }

    [Fact]
    public void Counts_if_select_cases_andalso_as_decision_points()
    {
        const string src = """
            Public Class C
                Public Function CalculateVat(amount As Decimal, region As String, isExempt As Boolean) As Decimal
                    If isExempt Then Return 0D
                    Dim rate As Decimal
                    Select Case region
                        Case "NO"
                            rate = 0.25D
                        Case "UK"
                            rate = 0.2D
                        Case "DE"
                            rate = 0.19D
                        Case Else
                            rate = 0D
                    End Select
                    If amount > 10000D AndAlso Not isExempt Then
                        rate += 0.01D
                    End If
                    Return amount * rate
                End Function
            End Class
            """;
        // If(1) + Case NO/UK/DE(3, Case Else excluded) + If(1) + AndAlso(1) = 6 decisions → 7.
        Assert.Equal(7, Complexity(src, "CalculateVat"));
    }

    [Fact]
    public void Straight_line_method_is_complexity_one()
    {
        const string src = """
            Public Class C
                Public Function Add(a As Integer, b As Integer) As Integer
                    Return a + b
                End Function
            End Class
            """;
        Assert.Equal(1, Complexity(src, "Add"));
    }
}

public class VbSourceScanTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pinion.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public async Task Source_scan_fallback_analyzes_vb_without_msbuild()
    {
        // The test host doesn't register MSBuild, so MSBuildWorkspace.Create() fails and the adapter
        // falls back to scanning .vb files directly — the path that makes VB analyze work on real legacy
        // (non-SDK) projects. It must still produce correct IR (members, complexity, domain tags).
        string vbproj = Path.Combine(RepoRoot(), "samples", "LegacyVb", "LegacyVb.vbproj");

        var units = await new VisualBasicAdapter().AnalyzeAsync(vbproj, default);

        var vat = units.FirstOrDefault(u => u.DisplayName == "InvoiceService.CalculateVat");
        Assert.NotNull(vat);
        Assert.Equal(7, vat!.CyclomaticComplexity);            // VB complexity computed from raw source
        Assert.Contains("money", vat.DomainTags);              // name-based tagging, reused engine logic
        Assert.True(vat.IsPublicEntryPoint);

        // Call graph (blast radius): GrossTotal calls CalculateVat, so CalculateVat has a caller and
        // GrossTotal has CalculateVat as a callee.
        var gross = units.First(u => u.DisplayName == "InvoiceService.GrossTotal");
        Assert.Contains(vat.Id, gross.CalleeIds);
        Assert.Contains(gross.Id, vat.CallerIds);
    }

    [Fact]
    public async Task Tested_method_is_detected_via_a_referencing_test_project()
    {
        // A production VB project + a "Tests" project that references it and calls Add (but not Unused).
        // The adapter must mark Add as HasTests and leave Unused untested. Built in-memory so it's
        // deterministic (no dependence on MSBuild being registered in this run).
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        // Exclude test-framework assemblies — the test host's TPA contains xunit/testplatform, which would
        // otherwise make IsTestProject (correctly) classify the production project as a test project too.
        string[] testMarkers = { "xunit", "testplatform", "testhost", "mstest", "nunit" };
        var refs = tpa
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p => !testMarkers.Any(m => Path.GetFileName(p).Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

        var ws = new AdhocWorkspace();
        var prodId = ProjectId.CreateNewId();
        var sol = ws.CurrentSolution.AddProject(ProjectInfo.Create(
            prodId, VersionStamp.Default, "Prod", "Prod", LanguageNames.VisualBasic, metadataReferences: refs));
        sol = sol.AddDocument(DocumentId.CreateNewId(prodId), "Calc.vb", SourceText.From("""
            Namespace M
                Public Class Calc
                    Public Function Add(a As Integer, b As Integer) As Integer
                        Return a + b
                    End Function
                    Public Function Unused(x As Integer) As Integer
                        Return x
                    End Function
                End Class
            End Namespace
            """));

        var testId = ProjectId.CreateNewId();
        sol = sol.AddProject(ProjectInfo.Create(
            testId, VersionStamp.Default, "ProdTests", "ProdTests", LanguageNames.VisualBasic,
            metadataReferences: refs, projectReferences: new[] { new ProjectReference(prodId) }));
        sol = sol.AddDocument(DocumentId.CreateNewId(testId), "CalcTests.vb", SourceText.From("""
            Namespace M
                Public Class CalcTests
                    Public Sub AddsTwoNumbers()
                        Dim r = New Calc().Add(1, 2)
                    End Sub
                End Class
            End Namespace
            """));

        var units = await new VisualBasicAdapter().AnalyzeSolutionAsync(sol, targetProjectPath: null, default);

        string dump = "[" + string.Join(" | ", units.Select(u => $"{u.DisplayName} tests={u.HasTests}")) + "]";
        var add = units.FirstOrDefault(u => u.DisplayName == "Calc.Add");
        Assert.True(add is not null, "no Calc.Add — units: " + dump);
        Assert.True(add!.HasTests, "Add should be tested — units: " + dump);
        Assert.False(units.First(u => u.DisplayName == "Calc.Unused").HasTests);
        Assert.DoesNotContain(units, u => u.DisplayName == "CalcTests.AddsTwoNumbers"); // test members excluded
    }
}

public class VbMemberGatheringTests
{
    // An in-memory single VB project (no MSBuild), with test-framework assemblies filtered from the refs
    // so the production project isn't itself classified as a test project.
    private static async Task<IReadOnlyList<CodeUnit>> Analyze(string vb)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        string[] testMarkers = { "xunit", "testplatform", "testhost", "mstest", "nunit" };
        var refs = tpa
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p => !testMarkers.Any(m => Path.GetFileName(p).Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

        var ws = new AdhocWorkspace();
        var id = ProjectId.CreateNewId();
        var sol = ws.CurrentSolution.AddProject(ProjectInfo.Create(
            id, VersionStamp.Default, "Prod", "Prod", LanguageNames.VisualBasic, metadataReferences: refs));
        sol = sol.AddDocument(DocumentId.CreateNewId(id), "Src.vb", SourceText.From(vb));
        return await new VisualBasicAdapter().AnalyzeSolutionAsync(sol, targetProjectPath: null, default);
    }

    [Fact]
    public async Task Computed_property_getter_is_analyzed_but_auto_property_is_not()
    {
        // A property Get with real branch logic carries behaviour and must appear (with its decision
        // counted); an auto-property has no accessor body and must be skipped. This is the parity gap with
        // the C# adapter that silently dropped VB property logic from the readiness report.
        var units = await Analyze("""
            Namespace M
                Public Class Account
                    Private _balance As Decimal
                    Public ReadOnly Property Status As String
                        Get
                            If _balance < 0D Then Return "overdrawn"
                            Return "ok"
                        End Get
                    End Property
                    Public Property Name As String
                End Class
            End Namespace
            """);

        string dump = "[" + string.Join(" | ", units.Select(u => u.DisplayName)) + "]";
        var status = units.FirstOrDefault(u => u.DisplayName == "Account.Status.get");
        Assert.True(status is not null, "computed property getter not analyzed — units: " + dump);
        Assert.Equal(2, status!.CyclomaticComplexity);           // one If → 2
        Assert.DoesNotContain(units, u => u.DisplayName.StartsWith("Account.Name")); // auto-property skipped
    }

    [Fact]
    public async Task User_defined_operator_is_analyzed()
    {
        var units = await Analyze("""
            Namespace M
                Public Class Money
                    Public Shared Operator +(a As Money, b As Money) As Money
                        Return New Money()
                    End Operator
                End Class
            End Namespace
            """);

        Assert.Contains(units, u => u.DisplayName.StartsWith("Money.op_"));
    }
}

public class VbLandmineDetectorTests
{
    private static IReadOnlyList<string> Detect(
        string[]? imports = null, string[]? bases = null, string[]? attrs = null, string file = "X.vb") =>
        VbLandmineDetector.Detect(
            imports ?? Array.Empty<string>(), bases ?? Array.Empty<string>(), attrs ?? Array.Empty<string>(), file);

    [Fact]
    public void Wcf_detected_via_import_or_attribute()
    {
        Assert.Contains("WCF", Detect(imports: new[] { "System.ServiceModel" }));
        Assert.Contains("WCF", Detect(attrs: new[] { "ServiceContract" }));
    }

    [Fact]
    public void WebForms_detected_via_import_base_or_vb_codebehind_filename()
    {
        Assert.Contains("WebForms", Detect(imports: new[] { "System.Web.UI" }));
        Assert.Contains("WebForms", Detect(bases: new[] { "Page" }));
        Assert.Contains("WebForms", Detect(file: "Default.aspx.vb")); // VB code-behind suffix
    }

    [Fact]
    public void Ef6_detected_but_not_ef_core()
    {
        Assert.Contains("EF6", Detect(imports: new[] { "System.Data.Entity" }));
        Assert.DoesNotContain("EF6", Detect(imports: new[] { "Microsoft.EntityFrameworkCore" }, bases: new[] { "DbContext" }));
    }
}
