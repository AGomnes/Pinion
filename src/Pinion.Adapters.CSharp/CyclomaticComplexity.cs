using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Cyclomatic complexity by counting decision points on the syntax tree: if/else-if, case labels and
/// their <c>when</c> guards, switch-expression arms, <c>&amp;&amp;</c>/<c>||</c>/<c>??</c>/<c>??=</c>,
/// <c>?:</c>, <c>and</c>/<c>or</c> patterns, loops, and catch (+ catch <c>when</c> filters). The metric
/// is "number of independent paths" = decision points + 1.
/// </summary>
internal static class CyclomaticComplexity
{
    /// <summary>Computes complexity for a member's body. A bodyless member is complexity 1.</summary>
    public static int Compute(SyntaxNode? body)
    {
        if (body is null) return 1;

        int decisions = 0;
        // Don't descend into nested local functions: they are analyzed as their own units, so their
        // decision points must not also inflate the method that declares them. (Lambdas are NOT separate
        // units, so their branches deliberately stay attributed to the enclosing member.)
        foreach (var node in body.DescendantNodesAndSelf(descendIntoChildren: n => n == body || n is not LocalFunctionStatementSyntax))
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case DoStatementSyntax:
                case CaseSwitchLabelSyntax:        // each `case x:` in a switch statement
                case CasePatternSwitchLabelSyntax: // `case Foo f:` (its `when` guard counted via WhenClauseSyntax)
                case SwitchExpressionArmSyntax:    // each arm of a switch expression
                case ConditionalExpressionSyntax:  // ?:
                case CatchClauseSyntax:            // each catch
                case CatchFilterClauseSyntax:      // `catch ... when (guard)` — the guard is a second branch
                case WhenClauseSyntax:             // `case ... when (guard)` / switch-arm guard
                    decisions++;
                    break;

                case BinaryExpressionSyntax bin
                    when bin.IsKind(SyntaxKind.LogicalAndExpression)
                      || bin.IsKind(SyntaxKind.LogicalOrExpression)
                      || bin.IsKind(SyntaxKind.CoalesceExpression): // && || ?? each introduce a branch
                    decisions++;
                    break;

                case AssignmentExpressionSyntax asg
                    when asg.IsKind(SyntaxKind.CoalesceAssignmentExpression): // ??= short-circuits
                    decisions++;
                    break;

                case BinaryPatternSyntax pat
                    when pat.IsKind(SyntaxKind.AndPattern) || pat.IsKind(SyntaxKind.OrPattern): // `is > 0 and < 10`
                    decisions++;
                    break;
            }
        }

        return decisions + 1;
    }
}
