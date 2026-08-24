using System.Text.RegularExpressions;
using Pinion.Adapters.CSharp.Generate;
using Xunit;

namespace Pinion.Tests;

public class RegexSamplerTests
{
    [Theory]
    [InlineData(@"^[A-Z]{3}-\d{4}$")]
    [InlineData(@"\d{3}-\d{2}-\d{4}")]
    [InlineData(@"^[a-z]+@[a-z]+\.[a-z]{2,}$")]
    [InlineData(@"^(GB|US|NO)\d{6}$")]
    [InlineData(@"^[A-Za-z0-9_]{4,12}$")]
    [InlineData(@"colou?r")]
    [InlineData(@"a*bc")]
    [InlineData(@"^\+?\d{10,15}$")]
    public void Generated_string_matches_the_pattern(string pattern)
    {
        string? m = RegexSampler.GenerateMatch(pattern);
        Assert.NotNull(m);
        Assert.Matches(pattern, m!);
    }

    [Fact]
    public void Unsupported_constructs_return_null_not_a_wrong_string()
    {
        Assert.Null(RegexSampler.GenerateMatch(@"^(?=.*\d).{8,}$"));
    }

    [Fact]
    public void NonMatch_is_verified_to_not_match()
    {
        const string pat = @"^[A-Z]{3}-\d{4}$";
        string m = RegexSampler.GenerateMatch(pat)!;
        string? nm = RegexSampler.GenerateNonMatch(pat, m);

        Assert.NotNull(nm);
        Assert.DoesNotMatch(pat, nm!);
    }

    [Fact]
    public void Result_is_deterministic()
    {
        const string pat = @"^[A-Z]{3}-\d{4}$";
        Assert.Equal(RegexSampler.GenerateMatch(pat), RegexSampler.GenerateMatch(pat));
    }
}
