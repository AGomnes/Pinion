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

        // It builds a real CartLine list rather than passing null.
        Assert.Contains("new global::LegacyShop.CartLine(", source);
        Assert.DoesNotContain("default(IReadOnlyList", source);
        // Object-field synthesis: the CartLine's FIELDS are varied with mined constants so branches
        // that test them get reached — a mined quantity tier and the "CLEARANCE" SKU that gates the
        // clearance discount (mined from `line.Sku.StartsWith("CLEARANCE")`, a method-call argument).
        Assert.Contains(", 10, ", source);     // line.Quantity >= 10 tier
        Assert.Contains("\"CLEARANCE\"", source);
        // And it's a Verify characterization test with no hand-written expected values.
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

        // Switch-case constants are used as inputs (reaches each region branch).
        Assert.Contains("\"NO\"", source);
        Assert.Contains("\"UK\"", source);
        Assert.Contains("\"DE\"", source);
        // The comparison threshold (amount > 10000m) and its boundary neighbours are used.
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
        Assert.Contains("out __o", src);            // captured, not discarded
        Assert.Contains("value = (object?)", src);  // the out value is recorded in the entry
    }

    [Fact]
    public void Ref_parameter_uses_a_temp()
    {
        string src = Synth("Bump");
        Assert.Contains("ref __r", src);
        Assert.Contains("__r0 = ", src);        // a declared, assigned temp
        Assert.DoesNotContain("var __r", src);  // explicit type — a null/default candidate must still compile (CS0815)
    }

    [Fact]
    public void Float_parameter_uses_float_literals_not_double()
    {
        string src = Synth("Scale");
        Assert.Contains("1.5f", src);       // mined float boundary, f-suffixed
        Assert.DoesNotContain("1.5d", src); // never a double literal where a float is required (CS0664)
    }

    [Fact]
    public void Mined_string_constant_with_newline_is_escaped_not_raw()
    {
        string src = Synth("Multiline");
        Assert.Contains("line1", src);                 // the mined constant is present...
        Assert.DoesNotContain("line1\nline2", src);    // ...but never as a raw newline inside the literal (CS1010)
    }

    [Fact]
    public void NonFinite_double_constant_emits_named_constant_not_invalid_literal()
    {
        string src = Synth("Classify");
        Assert.Contains("double.PositiveInfinity", src); // a valid literal form
        Assert.DoesNotContain("Infinityd", src);         // not the bare, non-compiling form
    }

    [Fact]
    public void Required_member_type_gets_an_object_initializer()
    {
        string src = Synth("Ship");
        Assert.Contains("new global::LegacyShop.Shipment()", src); // constructed...
        Assert.Contains("Destination =", src);                     // ...with the required member set (else CS9035)
    }

    [Fact]
    public void Generated_file_is_portable_across_host_nullable_settings()
    {
        // The file uses its own `object?` annotations AND feeds null candidates, so it must fix its own
        // nullable context — else CS8632 under <Nullable>disable</Nullable> or CS8625 under enable+warnaserrors.
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

        // ValidateToken requires a 16+ char string with a letter and a digit; the generator must
        // produce an input that clears that guard (length derived from the mined `16` constant).
        Assert.Contains("\"Ab1Ab1Ab1Ab1Ab1A\"", source);
    }

    [Fact]
    public void Conjunctive_string_guards_get_boundary_near_misses()
    {
        string sampleRoot = Path.Combine(RepoRoot(), "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "AuthHandler.cs");
        var unit = UnitAt(file, "AuthHandler.ValidateToken", "ValidateToken");

        string src = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        // The happy-path witness still clears the full conjunction...
        Assert.Contains("\"Ab1Ab1Ab1Ab1Ab1A\"", src);
        // ...but now each guard also gets a near-miss the old one-hot generator never produced — so a
        // mutation to any single guard is killed by the input that isolates it:
        Assert.Contains("\"Ab1Ab1Ab1Ab1Ab1\"", src);   // length 15 — under the 16-char floor
        Assert.Contains("\"AbcAbcAbcAbcAbcA\"", src);   // 16 letters, no digit
        Assert.Contains("\"1231231231231231\"", src);   // 16 digits, no letter
        Assert.Contains("\"-Ab1Ab1Ab1Ab1Ab1A\"", src); // starts with '-'
    }

    [Fact]
    public void Property_based_sampling_adds_joint_random_rows_for_numeric_methods()
    {
        string root = RepoRoot();
        string sampleRoot = Path.Combine(root, "samples", "LegacyShop");
        string file = Path.Combine(sampleRoot, "src", "LegacyShop", "InvoiceService.cs");
        var unit = UnitAt(file, "InvoiceService.CalculateVat", "CalculateVat");

        string source = new CSharpTestGenerator().SynthesizeDeterministic(unit, sampleRoot, default);

        // The property tier (CsCheck) appends deterministic joint-random rows, so a numeric method
        // ends up with more input rows than the one-hot generator's cap (12) alone could produce.
        int rows = System.Text.RegularExpressions.Regex.Matches(source, @"= sut\.CalculateVat\(").Count;
        Assert.True(rows > 12, $"expected property sampling to add rows beyond the one-hot cap; got {rows}");

        // Joint sampling reaches a real tax region with isExempt=false — impossible from one-hot rows,
        // whose base holds isExempt at its first candidate (true) and returns 0m for every region.
        Assert.Matches(@", ""(NO|UK|DE|FR)"", false\)", source);
    }

    [Fact]
    public void Static_factory_recovers_a_real_receiver_for_private_ctor_types()
    {
        // Formatter has a private ctor and is obtained via `Formatter.Default` (the NodaTime shape).
        // The generator must use that static factory as the receiver, not `default(Formatter)!` (null,
        // which would capture only a NullReferenceException and lock in nothing).
        string src = Synth("Render");
        Assert.Contains("var sut = global::LegacyShop.Formatter.Default;", src);
        Assert.DoesNotContain("default(global::LegacyShop.Formatter)", src);
    }

    [Fact]
    public void Snapshot_tolerates_return_types_whose_getters_throw()
    {
        // Wrap returns ResultBox, whose Value getter throws on failure. Verify must be told to skip
        // members that throw, else serializing the result blows up the whole snapshot (the
        // "snapshot not captured" failure seen on NodaTime's ParseResult<T>).
        string src = Synth("Wrap");
        Assert.Contains("settings.IgnoreMembersThatThrow<Exception>();", src);
        Assert.Contains("await Verify(entries, settings);", src);
    }

    [Fact]
    public void Framework_abstractions_get_a_real_value_not_null()
    {
        // ToStringInvariant takes an IFormatProvider; the synthesizer must pass a real culture, not
        // default(IFormatProvider)! (null), so the method records real formatted behaviour, not an NRE.
        string src = Synth("ToStringInvariant");
        Assert.Contains("global::System.Globalization.CultureInfo.InvariantCulture", src);
        Assert.DoesNotContain("default(global::System.IFormatProvider)", src);
    }

    [Fact]
    public void Service_interface_dependency_is_stubbed_not_null()
    {
        // PriceQuoter injects IRate (a service interface with no construction path). The synthesizer
        // must build a stub implementing it, so Quote() runs with a no-op collaborator and records real
        // behaviour — instead of `new PriceQuoter(default(IRate)!)` which NREs on first use.
        string src = Synth("Quote");
        Assert.Contains("new __Stub_IRate", src);                       // receiver gets a stubbed collaborator
        Assert.Contains("private sealed class __Stub_IRate", src);      // the stub class is emitted inline
        Assert.DoesNotContain("default(global::LegacyShop.IRate)", src); // not null-injected
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

        Assert.Equal(a, b); // identical source in → identical test out (reproducible golden masters)
    }
}
