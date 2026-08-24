using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class TargetGuardsTests
{
    private static CodeUnit Unit(string display = "Svc.M", string file = "C:/repo/src/Svc.cs", params string[] tags) =>
        new($"N.{display}()", display, file, 1, 5, "sig", System.Array.Empty<ParamInfo>(), "void",
            1, 5, System.Array.Empty<string>(), System.Array.Empty<string>(), tags,
            false, true, System.Array.Empty<string>());

    [Theory]
    [InlineData(DomainTag.Io, true)]
    [InlineData(DomainTag.Money, false)]
    [InlineData(DomainTag.Auth, false)]
    [InlineData(DomainTag.Date, false)]
    public void SideEffecting_flags_io_only(string tag, bool expected)
    {
        Assert.Equal(expected, TargetGuards.IsSideEffecting(Unit(tags: tag)));
    }

    [Fact]
    public void Untagged_method_is_not_side_effecting()
    {
        Assert.False(TargetGuards.IsSideEffecting(Unit()));
    }

    [Theory]
    [InlineData(DomainTag.Money, true)]
    [InlineData(DomainTag.Auth, true)]
    [InlineData(DomainTag.Io, false)]
    [InlineData(DomainTag.Date, false)]
    public void Sensitive_flags_money_and_auth(string tag, bool expected)
    {
        Assert.Equal(expected, TargetGuards.IsSensitive(Unit(tags: tag)));
    }

    [Fact]
    public void Exclusion_matches_by_display_name_substring()
    {
        var unit = Unit("PriceEngine.ApplyDiscounts", "C:/repo/src/PriceEngine.cs");
        Assert.True(TargetGuards.IsExcluded(unit, new[] { "ApplyDiscounts" }));
        Assert.False(TargetGuards.IsExcluded(unit, new[] { "Checkout" }));
    }

    [Fact]
    public void Exclusion_matches_by_file_glob()
    {
        var unit = Unit("PriceEngine.ApplyDiscounts", "C:/repo/src/Legacy/PriceEngine.cs");
        Assert.True(TargetGuards.IsExcluded(unit, new[] { "*/Legacy/*" }));
        Assert.True(TargetGuards.IsExcluded(unit, new[] { "PriceEngine.cs" }));
        Assert.False(TargetGuards.IsExcluded(unit, new[] { "*/Billing/*" }));
    }

    [Fact]
    public void Empty_patterns_exclude_nothing()
    {
        Assert.False(TargetGuards.IsExcluded(Unit(), System.Array.Empty<string>()));
    }

    [Fact]
    public void NeverSend_matches_by_namespace_prefix_and_file_glob()
    {
        var unit = Unit("Vault.Decrypt", "C:/repo/src/Secrets/Vault.cs");
        Assert.True(TargetGuards.IsNeverSend(unit, new[] { "N.Vault" }));
        Assert.True(TargetGuards.IsNeverSend(unit, new[] { "*/Secrets/*" }));
        Assert.False(TargetGuards.IsNeverSend(unit, new[] { "*/Public/*" }));
    }

    [Fact]
    public void Empty_patterns_send_everything()
    {
        Assert.False(TargetGuards.IsNeverSend(Unit(), System.Array.Empty<string>()));
    }
}
