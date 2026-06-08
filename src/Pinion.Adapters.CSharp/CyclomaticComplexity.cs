using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pinion.Adapters.CSharp;

/// <summary>
/// Cyclomatic complexity by counting decision points on the syntax tree, per the
/// spec: if/else-if, case labels, &amp;&amp;/||, ?:, loops, catch. The metric is
/// "number of independent paths" = decision points + 1.
/// </summary>
internal static class CyclomaticComplexity
{
    /// <summary>Computes complexity for a member's body. A bodyless member is complexity 1.</summary>
    public static int Compute(SyntaxNode? body)
    {
        if (body is null) return 1;

        int decisions = 0;
        foreach (var node in body.DescendantNodesAndSelf())
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
                case CasePatternSwitchLabelSyntax: // `case Foo f when ...:`
                case SwitchExpressionArmSyntax:    // each arm of a switch expression
                case ConditionalExpressionSyntax:  // ?:
                case CatchClauseSyntax:
                    decisions++;
                    break;

                case BinaryExpressionSyntax bin
                    when bin.IsKind(SyntaxKind.LogicalAndExpression)
                      || bin.IsKind(SyntaxKind.LogicalOrExpression):
                    decisions++;
                    break;

                case BinaryExpressionSyntax bin
                    when bin.IsKind(SyntaxKind.CoalesceExpression): // ?? introduces a branch
                    decisions++;
                    break;
            }
        }

        return decisions + 1;
    }
}
