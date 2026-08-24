using Pinion.Adapters.CSharp.Generate;
using Pinion.Adapters.VisualBasic;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// VB `generate`: the synthesizer resolves + mines from VB syntax and emits a C# characterization test
/// that calls the VB assembly. These run against the bundled LegacyVb sample over the source-scan path
/// (the test host registers no MSBuild), exactly like a legacy non-SDK .vbproj would load.
/// </summary>
public class VbGenerateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pinion.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static async Task<(CodeUnit Unit, string SampleDir)> UnitFor(string displayName)
    {
        string sample = Path.Combine(RepoRoot(), "samples", "LegacyVb");
        var units = await new VisualBasicAdapter().AnalyzeAsync(Path.Combine(sample, "LegacyVb.vbproj"), default);
        var unit = units.FirstOrDefault(u => u.DisplayName == displayName);
        Assert.True(unit is not null, $"{displayName} not found; units: {string.Join(", ", units.Select(u => u.DisplayName))}");
        return (unit!, sample);
    }

    [Fact]
    public async Task Vb_method_synthesizes_a_csharp_test_with_mined_vb_constants()
    {
        var (unit, sample) = await UnitFor("InvoiceService.CalculateVat");

        using var synth = new CSharpDeterministicSynthesizer();
        string src = await synth.SynthesizeAsync(unit, sample, default);

        Assert.Contains("// pinion-format:", src);
        Assert.Contains("namespace Pinion.Generated;", src);
        Assert.Contains("var sut = new global::LegacyVb.InvoiceService()", src);
        Assert.Contains("\"NO\"", src);
        Assert.Contains("\"UK\"", src);
        Assert.Contains("10000", src);
        Assert.Contains("true", src);
    }

    [Fact]
    public async Task Vb_sub_new_constructor_and_gross_total_also_synthesize()
    {
        var (unit, sample) = await UnitFor("InvoiceService.GrossTotal");

        using var synth = new CSharpDeterministicSynthesizer();
        string src = await synth.SynthesizeAsync(unit, sample, default);

        Assert.Contains("sut.GrossTotal(0m", src);
    }
}
