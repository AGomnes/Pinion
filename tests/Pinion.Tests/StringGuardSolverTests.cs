using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pinion.Adapters.CSharp.Generate;
using Xunit;

namespace Pinion.Tests;

public class StringGuardSolverTests
{
    private static StringGuardSolver.StringGuards Extract(string methodSource, string param)
    {
        var tree = CSharpSyntaxTree.ParseText("class C { " + methodSource + " }");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        SyntaxNode body = (SyntaxNode?)method.Body ?? method.ExpressionBody!;
        return StringGuardSolver.Extract(body, param);
    }

    [Fact]
    public void Extracts_length_charclass_and_prefix_guards()
    {
        // The AuthHandler.ValidateToken shape: length window + has-letter + has-digit + not-startswith.
        var g = Extract("""
            bool M(string token) {
                if (string.IsNullOrWhiteSpace(token)) return false;
                if (token.Length < 16 || token.Length > 256) return false;
                foreach (char c in token) { if (char.IsLetter(c)) {} else if (char.IsDigit(c)) {} }
                return !token.StartsWith("-");
            }
            """, "token");

        Assert.Contains(16, g.Lengths);
        Assert.Contains(256, g.Lengths);
        Assert.True(g.RequireLetter);
        Assert.True(g.RequireDigit);
        Assert.Equal("-", g.Prefix);
        Assert.True(g.Any);
    }

    [Fact]
    public void Synthesizes_a_witness_and_a_near_miss_for_each_guard()
    {
        var g = Extract("""
            bool M(string token) {
                if (token.Length < 16 || token.Length > 256) return false;
                foreach (char c in token) { if (char.IsLetter(c)) {} else if (char.IsDigit(c)) {} }
                return !token.StartsWith("-");
            }
            """, "token");

        var c = StringGuardSolver.Candidates(g);

        Assert.Contains(c, s => s.Length == 16 && s.Any(char.IsLetter) && s.Any(char.IsDigit)); // clears the conjunction
        Assert.Contains(c, s => s.Length == 15);                          // under the length floor
        Assert.Contains(c, s => s.Length == 16 && !s.Any(char.IsLetter)); // all digits → fails has-letter
        Assert.Contains(c, s => s.Length == 16 && !s.Any(char.IsDigit));  // all letters → fails has-digit
        Assert.Contains(c, s => s.StartsWith("-"));                       // exercises the StartsWith guard
        Assert.Contains(c, s => s.Length == 257);                        // just over the upper bound
    }

    [Fact]
    public void Extracts_required_prefix_contains_and_suffix_for_a_code_format()
    {
        var g = Extract("""
            bool M(string sku) {
                if (!sku.StartsWith("PRD-")) return false;
                if (!sku.Contains("X")) return false;
                return sku.EndsWith("9");
            }
            """, "sku");

        Assert.Equal("PRD-", g.Prefix);
        Assert.Contains("X", g.Contains);
        Assert.Equal("9", g.Suffix);

        var c = StringGuardSolver.Candidates(g);
        Assert.Contains(c, s => s.StartsWith("PRD-")); // covers the has-affix side regardless of polarity
        Assert.Contains(c, s => s.Contains("X"));
        Assert.Contains(c, s => s.EndsWith("9"));
    }

    [Fact]
    public void A_pure_equality_string_is_left_to_the_constant_miner()
    {
        // No length / char-class / affix guard — only `== "literal"`. The solver must NOT engage (the
        // mined-strings path already covers this), so behavior for switch/equality strings is unchanged.
        var g = Extract("""
            string M(string region) {
                if (region == "NO") return "n";
                if (region == "UK") return "u";
                return "other";
            }
            """, "region");

        Assert.False(g.Any);
        Assert.Equal(new[] { "NO", "UK" }, g.Exact);
    }

    [Fact]
    public void Candidates_are_deterministic()
    {
        var g = Extract("bool M(string p) { return p.Length >= 8 && p.StartsWith(\"A\"); }", "p");
        Assert.Equal(StringGuardSolver.Candidates(g), StringGuardSolver.Candidates(g));
    }
}
