using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Pinion.Adapters.VisualBasic;

/// <summary>
/// Cyclomatic complexity for VB: decision points + 1. Counts If / ElseIf / single-line If, each
/// non-Else Case, While / For / For Each / Do…Loop, each Catch and its <c>When</c> filter, the
/// short-circuit operators <c>AndAlso</c>/<c>OrElse</c>, and the <c>If(…)</c> conditional/coalescing
/// expressions. The VB analog of the C# CyclomaticComplexity.
/// </summary>
internal static class VbComplexity
{
    public static int Compute(MethodBlockBaseSyntax? body)
    {
        if (body is null) return 1;

        int decisions = 0;
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case SingleLineIfStatementSyntax:
                case MultiLineIfBlockSyntax:
                case ElseIfBlockSyntax:
                case WhileBlockSyntax:
                case ForBlockSyntax:
                case ForEachBlockSyntax:
                case DoLoopBlockSyntax:
                case CatchBlockSyntax:
                case CatchFilterClauseSyntax:
                case TernaryConditionalExpressionSyntax:
                case BinaryConditionalExpressionSyntax:
                    decisions++;
                    break;

                case CaseBlockSyntax cb when !cb.IsKind(SyntaxKind.CaseElseBlock):
                    decisions++;
                    break;

                case BinaryExpressionSyntax be
                    when be.IsKind(SyntaxKind.AndAlsoExpression) || be.IsKind(SyntaxKind.OrElseExpression):
                    decisions++;
                    break;
            }
        }

        return decisions + 1;
    }
}
