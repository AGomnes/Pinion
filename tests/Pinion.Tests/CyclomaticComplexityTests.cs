using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

public class CyclomaticComplexityTests
{
    private static int ComplexityOf(string methodSource)
    {
        var tree = CSharpSyntaxTree.ParseText("class C { " + methodSource + " }");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        return CyclomaticComplexity.Compute(body);
    }

    [Fact]
    public void Straight_line_method_is_one()
    {
        Assert.Equal(1, ComplexityOf("int M() { return 1; }"));
    }

    [Fact]
    public void Each_if_adds_one()
    {
        Assert.Equal(2, ComplexityOf("int M(int x) { if (x > 0) return 1; return 0; }"));
    }

    [Fact]
    public void Logical_operators_each_add_a_path()
    {
        Assert.Equal(4, ComplexityOf("bool M(bool a, bool b, bool c) { if (a && b || c) return true; return false; }"));
    }

    [Fact]
    public void Switch_cases_loops_and_ternary_all_count()
    {
        const string src = @"
            int M(int x) {
                for (int i = 0; i < x; i++) { }      // +1
                switch (x) {
                    case 1: return 1;                 // +1
                    case 2: return 2;                 // +1
                    default: break;
                }
                return x > 0 ? 1 : 0;                 // +1 (ternary)
            }";
        Assert.Equal(5, ComplexityOf(src));
    }

    [Fact]
    public void Coalesce_and_coalesce_assignment_each_add_a_path()
    {
        Assert.Equal(3, ComplexityOf("string M(string a, string b) { var x = a ?? b; x ??= b; return x; }"));
    }

    [Fact]
    public void Pattern_and_or_combinators_each_add_a_path()
    {
        Assert.Equal(3, ComplexityOf("bool M(int x) { if (x is > 0 and < 10) return true; return false; }"));
    }

    [Fact]
    public void Case_and_catch_when_guards_each_add_a_path()
    {
        const string src = @"
            int M(object o) {
                try {
                    switch (o) {
                        case int i when i > 0: return i;   // case +1, when +1
                        default: return 0;
                    }
                }
                catch (System.Exception) when (o != null) { return -1; }  // catch +1, when +1
            }";
        Assert.Equal(5, ComplexityOf(src));
    }

    [Fact]
    public void Nested_local_function_decisions_are_not_counted_in_the_parent()
    {
        const string src = @"
            int M(int x) {
                if (x > 0) return 1;                                  // +1 (parent)
                int Helper(int y) { if (y > 5) return 2; return 3; }  // excluded from the parent
                return Helper(x);
            }";
        Assert.Equal(2, ComplexityOf(src));
    }
}
