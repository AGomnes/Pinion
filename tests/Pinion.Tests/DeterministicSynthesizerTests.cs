using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class DeterministicSynthesizerTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pinion.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static CodeUnit UnitAt(string file, string displayName, string methodName)
    {
        var lines = File.ReadAllLines(file);
        int idx = Array.FindIndex(lines, l => l.Contains("public ")
            && (l.Contains(methodName + "(") || l.Contains(methodName + "<")));
        Assert.True(idx >= 0, $"could not find {methodName} in {file}");
        int startLine = idx + 1;
        return new CodeUnit($"S.{displayName}({methodName})", displayName, file, startLine, startLine + 30,
            "sig", Array.Empty<ParamInfo>(), "void", 1, 30,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, true, Array.Empty<string>());
    }

    [Fact]
    public void Constructs_real_objects_and_collections_no_AI()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "PriceEngine.cs");

        var unit = UnitAt(file, "PriceEngine.ApplyDiscounts", "ApplyDiscounts");
        string source = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        Assert.Contains("new global::LegacyShop.CartLine(", source);
        Assert.DoesNotContain("default(IReadOnlyList", source);
        Assert.Contains(", 10, ", source);
        Assert.Contains("\"CLEARANCE\"", source);
        Assert.Contains("await Verify(entries, settings)", source);
        Assert.DoesNotContain("Assert.Equal", source);
    }

    [Fact]
    public void Mines_branch_constants_to_reach_real_branches()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "InvoiceService.cs");
        var unit = UnitAt(file, "InvoiceService.CalculateVat", "CalculateVat");

        string source = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        Assert.Contains("\"NO\"", source);
        Assert.Contains("\"UK\"", source);
        Assert.Contains("\"DE\"", source);
        Assert.Contains("10000m", source);
        Assert.Contains("9999m", source);
        Assert.Contains("10001m", source);
    }

    private static string HardCases() =>
        Path.Combine(RepoRoot(), "samples", "LegacyShop", "src", "LegacyShop", "HardCases.cs");

    private static string Synth(string method) =>
        new CSharpTestGenerator().SynthesizeDeterministic(
            UnitAt(HardCases(), $"HardCases.{method}", method),
            Path.Combine(RepoRoot(), "samples", "LegacyShop"), default);

    [Fact]
    public void Async_method_is_awaited()
    {
        string src = Synth("CountUpAsync");
        Assert.Contains("await sut.CountUpAsync(", src);
        Assert.Contains("var result = await", src);
    }

    [Fact]
    public void Out_parameter_value_is_captured_in_the_snapshot()
    {
        string src = Synth("TryDouble");
        Assert.Contains("out __o", src);
        Assert.Contains("value = (object?)", src);
    }

    [Fact]
    public void Ref_parameter_uses_a_temp()
    {
        string src = Synth("Bump");
        Assert.Contains("ref __r", src);
        Assert.Contains("__r0 = ", src);
        Assert.DoesNotContain("var __r", src);
    }

    [Fact]
    public void Float_parameter_uses_float_literals_not_double()
    {
        string src = Synth("Scale");
        Assert.Contains("1.5f", src);
        Assert.DoesNotContain("1.5d", src);
    }

    [Fact]
    public void Mined_string_constant_with_newline_is_escaped_not_raw()
    {
        string src = Synth("Multiline");
        Assert.Contains("line1", src);
        Assert.DoesNotContain("line1\nline2", src);
    }

    [Fact]
    public void NonFinite_double_constant_emits_named_constant_not_invalid_literal()
    {
        string src = Synth("Classify");
        Assert.Contains("double.PositiveInfinity", src);
        Assert.DoesNotContain("Infinityd", src);
    }

    [Fact]
    public void Required_member_type_gets_an_object_initializer()
    {
        string src = Synth("Ship");
        Assert.Contains("new global::LegacyShop.Shipment()", src);
        Assert.Contains("Destination =", src);
    }

    [Fact]
    public void Generated_file_is_portable_across_host_nullable_settings()
    {
        string src = Synth("TryDouble");
        Assert.Contains("#nullable enable annotations", src);
        Assert.Contains("#nullable disable warnings", src);
    }

    [Fact]
    public void Generic_method_is_skipped_cleanly_not_broken_output()
    {
        Assert.Throws<System.NotSupportedException>(() => Synth("Echo"));
    }

    [Fact]
    public void Private_method_is_skipped_cleanly()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "AuthHandler.cs");
        var lines = File.ReadAllLines(file);
        int idx = Array.FindIndex(lines, l => l.Contains("Normalize("));
        Assert.True(idx >= 0);

        var unit = new CodeUnit("S.AuthHandler.Normalize(string)", "AuthHandler.Normalize", file, idx + 1, idx + 1,
            "sig", Array.Empty<ParamInfo>(), "string", 1, 1,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, false, Array.Empty<string>());

        var ex = Assert.Throws<System.NotSupportedException>(() => new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default));
        Assert.Contains("public", ex.Message);
    }

    [Fact]
    public void Generates_a_guard_clearing_string_input()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "AuthHandler.cs");
        var unit = UnitAt(file, "AuthHandler.ValidateToken", "ValidateToken");

        string source = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        Assert.Contains("\"Ab1Ab1Ab1Ab1Ab1A\"", source);
    }

    [Fact]
    public void Conjunctive_string_guards_get_boundary_near_misses()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "AuthHandler.cs");
        var unit = UnitAt(file, "AuthHandler.ValidateToken", "ValidateToken");

        string src = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        Assert.Contains("\"Ab1Ab1Ab1Ab1Ab1A\"", src);
        Assert.Contains("\"Ab1Ab1Ab1Ab1Ab1\"", src);
        Assert.Contains("\"AbcAbcAbcAbcAbcA\"", src);
        Assert.Contains("\"1231231231231231\"", src);
        Assert.Contains("\"-Ab1Ab1Ab1Ab1Ab1A\"", src);
    }

    [Fact]
    public void Regex_gated_method_gets_a_matching_input_for_the_accept_path()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "SkuValidator.cs");
        var unit = UnitAt(file, "SkuValidator.ClassifySku", "ClassifySku");

        string src = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);
        Assert.Contains("\"AAA-5555\"", src);
    }

    [Fact]
    public void Property_based_sampling_adds_joint_random_rows_for_numeric_methods()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "InvoiceService.cs");
        var unit = UnitAt(file, "InvoiceService.CalculateVat", "CalculateVat");

        string source = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        int rows = System.Text.RegularExpressions.Regex.Matches(source, @"= sut\.CalculateVat\(").Count;
        Assert.True(rows > 12, $"expected property sampling to add rows beyond the one-hot cap; got {rows}");

        Assert.Matches(@", ""(NO|UK|DE|FR)"", false\)", source);
    }

    [Fact]
    public void Static_factory_recovers_a_real_receiver_for_private_ctor_types()
    {
        string src = Synth("Render");
        Assert.Contains("var sut = global::LegacyShop.Formatter.Default;", src);
        Assert.DoesNotContain("default(global::LegacyShop.Formatter)", src);
    }

    [Fact]
    public void Snapshot_tolerates_return_types_whose_getters_throw()
    {
        string src = Synth("Wrap");
        Assert.Contains("settings.IgnoreMembersThatThrow<Exception>();", src);
        Assert.Contains("await Verify(entries, settings);", src);
    }

    [Fact]
    public void Framework_abstractions_get_a_real_value_not_null()
    {
        string src = Synth("ToStringInvariant");
        Assert.Contains("global::System.Globalization.CultureInfo.InvariantCulture", src);
        Assert.DoesNotContain("default(global::System.IFormatProvider)", src);
    }

    [Fact]
    public void Concrete_List_parameter_uses_a_list_initializer_not_an_array()
    {
        string src = Synth("SumList");
        Assert.Contains("new global::System.Collections.Generic.List<int>", src);
        Assert.DoesNotContain("SumList(new[]", src);
    }

    [Fact]
    public void Service_interface_dependency_is_stubbed_not_null()
    {
        string src = Synth("Quote");
        Assert.Contains("new __Stub_IRate", src);
        Assert.Contains("private sealed class __Stub_IRate", src);
        Assert.DoesNotContain("default(global::LegacyShop.IRate)", src);
    }

    [Fact]
    public void Synthesis_is_deterministic()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "InvoiceService.cs");
        var unit = UnitAt(file, "InvoiceService.CalculateVat", "CalculateVat");

        var gen = new CSharpTestGenerator();
        string a = gen.SynthesizeDeterministic(unit, sampleRoot, default);
        string b = gen.SynthesizeDeterministic(unit, sampleRoot, default);

        Assert.Equal(a, b);
    }
}
