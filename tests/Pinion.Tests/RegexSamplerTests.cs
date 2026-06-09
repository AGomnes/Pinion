using System.Text.RegularExpressions;
using Pinion.Adapters.CSharp.Generate;
using Xunit;

namespace Pinion.Tests;

public class RegexSamplerTests
{
    [Theory]
    [InlineData(@"^[A-Z]{3}-\d{4}$")]            // product code
    [InlineData(@"\d{3}-\d{2}-\d{4}")]           // SSN-ish (unanchored)
    [InlineData(@"^[a-z]+@[a-z]+\.[a-z]{2,}$")]  // email-ish
    [InlineData(@"^(GB|US|NO)\d{6}$")]           // alternation
    [InlineData(@"^[A-Za-z0-9_]{4,12}$")]        // username
    [InlineData(@"colou?r")]                      // optional
    [InlineData(@"a*bc")]                          // star
    [InlineData(@"^\+?\d{10,15}$")]               // phone with optional +
    public void Generated_string_matches_the_pattern(string pattern)
    {
        string? m = RegexSampler.GenerateMatch(pattern);
        Assert.NotNull(m);
        Assert.Matches(pattern, m!);
    }

    [Fact]
    public void Unsupported_constructs_return_null_not_a_wrong_string()
    {
        // We don't model lookarounds — must bail to null, never emit a non-matching "match".
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
