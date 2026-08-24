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
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case ConditionalExpressionSyntax:
                case CatchClauseSyntax:
                case CatchFilterClauseSyntax:
                case WhenClauseSyntax:
                    decisions++;
                    break;

                case BinaryExpressionSyntax bin
                    when bin.IsKind(SyntaxKind.LogicalAndExpression)
                      || bin.IsKind(SyntaxKind.LogicalOrExpression)
                      || bin.IsKind(SyntaxKind.CoalesceExpression):
                    decisions++;
                    break;

                case AssignmentExpressionSyntax asg
                    when asg.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    decisions++;
                    break;

                case BinaryPatternSyntax pat
                    when pat.IsKind(SyntaxKind.AndPattern) || pat.IsKind(SyntaxKind.OrPattern):
                    decisions++;
                    break;
            }
        }

        return decisions + 1;
    }
}
