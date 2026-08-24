using System.Text.RegularExpressions;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Pinion.Engine.Reporting;
using Xunit;

namespace Pinion.Tests;

public class ChangeScopeTests
{
    private static readonly string F1 = Path.GetFullPath("/repo/src/Cart.cs");
    private static readonly string F2 = Path.GetFullPath("/repo/src/Pricing.cs");

    private static CodeUnit U(string id, string file, params string[] callerIds) =>
        new(id, id, file, 1, 5, "sig", Array.Empty<ParamInfo>(), "void", 1, 5,
            CallerIds: callerIds, CalleeIds: Array.Empty<string>(), DomainTags: Array.Empty<string>(),
            HasTests: false, IsPublicEntryPoint: true, MigrationLandmines: Array.Empty<string>());

    [Fact]
    public void Affected_includes_changed_file_methods_plus_their_callers()
    {
        var units = new[]
        {
            U("a", F1),
            U("b", F1, "c"),
            U("c", F2),
            U("e", F2),
        };

        var affected = ChangeScope.Affected(units, new[] { F1 }).Select(u => u.Id).ToHashSet();

        Assert.Equal(new[] { "a", "b", "c" }.ToHashSet(), affected);
        Assert.DoesNotContain("e", affected);
    }

    [Fact]
    public void Affected_walks_callers_transitively()
    {
        var units = new[]
        {
            U("b", F1, "c"),
            U("c", F2, "d"),
            U("d", F2),
            U("e", F2),
        };

        var affected = ChangeScope.Affected(units, new[] { F1 }).Select(u => u.Id).ToHashSet();

        Assert.Contains("d", affected);
        Assert.DoesNotContain("e", affected);
    }

    [Fact]
    public void Affected_is_empty_when_no_unit_lives_in_a_changed_file()
    {
        var units = new[] { U("a", F1), U("b", F2) };

        Assert.Empty(ChangeScope.Affected(units, new[] { Path.GetFullPath("/repo/src/Other.cs") }));
    }
}

public class CharacterizationNamingTests
{
    [Fact]
    public void TestClassName_has_the_expected_shape()
    {
        string name = CharacterizationNaming.TestClassName("InvoiceService.CalculateVat", "Ns.InvoiceService.CalculateVat(decimal)");
        Assert.Matches(new Regex("^InvoiceService_CalculateVat_[0-9a-f]{6}_CharacterizationTests$"), name);
    }

    [Fact]
    public void TestClassName_distinguishes_overloads_by_id()
    {
        string a = CharacterizationNaming.TestClassName("C.M", "Ns.C.M(int)");
        string b = CharacterizationNaming.TestClassName("C.M", "Ns.C.M(string)");
        Assert.NotEqual(a, b);
    }
}
