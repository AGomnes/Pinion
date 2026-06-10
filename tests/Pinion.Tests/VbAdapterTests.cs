using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Pinion.Adapters.VisualBasic;
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
