using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Pinion.Engine.Model;
using VbKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>
/// The VB half of deterministic synthesis. Resolution and constant mining read VB syntax; the OUTPUT is
/// still a C# characterization test (emitted by <see cref="CSharpDeterministicSynthesizer"/>'s
/// symbol-driven emitter), because C# calls a VB assembly natively — one golden-master pipeline for both
/// .NET languages. Mirrors the C# resolver/miner; the VB analyze adapter records a member's line as its
/// STATEMENT line, which is what resolution matches.
/// </summary>
internal static class VbSynthesis
{
    internal sealed record ResolvedVb(IMethodSymbol Method, MethodBlockBaseSyntax? Body, SemanticModel Model);

    /// <summary>Find the VB method/constructor for a unit by file + statement line + simple name.</summary>
    internal static async Task<ResolvedVb?> ResolveAsync(Solution solution, CodeUnit unit, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.VisualBasic) continue;
            var doc = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, unit.FilePath, StringComparison.OrdinalIgnoreCase));
            if (doc is null) continue;

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (model is null || root is null) continue;

            foreach (var stmt in root.DescendantNodes().OfType<MethodBaseSyntax>())
            {
                if (stmt is not (MethodStatementSyntax or SubNewStatementSyntax)) continue;
                if (stmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1 != unit.StartLine) continue;
                // The VB GetDeclaredSymbol overloads are per concrete statement type (RS1039 on the base).
                IMethodSymbol? symbol = stmt switch
                {
                    MethodStatementSyntax ms => model.GetDeclaredSymbol(ms, ct),
                    SubNewStatementSyntax sn => model.GetDeclaredSymbol(sn, ct),
                    _ => null,
                };
                if (symbol is null || !NameMatches(symbol, unit.SimpleName)) continue;
                return new ResolvedVb(symbol, stmt.Parent as MethodBlockBaseSyntax, model);
            }
        }
        return null;
    }

    private static bool NameMatches(IMethodSymbol m, string simpleName) =>
        string.Equals(m.Name, simpleName, StringComparison.OrdinalIgnoreCase) // VB is case-insensitive
        || (m.MethodKind == MethodKind.Constructor && simpleName is "ctor" or ".ctor");

    /// <summary>
    /// Mine the constants a VB body DECIDES on — comparison operands, <c>Select Case</c> clause values
    /// (simple, range, and <c>Case Is</c> relational), and gate-predicate arguments
    /// (<c>StartsWith</c>/<c>Contains</c>/…) — the same vocabulary as the C# miner, from VB syntax.
    /// </summary>
    internal static CSharpDeterministicSynthesizer.Mined MineConstants(
        MethodBlockBaseSyntax? body, SemanticModel model, CancellationToken ct)
    {
        var mined = new CSharpDeterministicSynthesizer.Mined();
        if (body is null) return mined;

        void Take(SyntaxNode? node)
        {
            if (node is null) return;
            var cv = model.GetConstantValue(node, ct);
            if (!cv.HasValue) return;
            switch (cv.Value)
            {
                case string s: mined.Strings.Add(s); break;
                case int i: mined.Ints.Add(i); break;
                case long l: mined.Ints.Add(l); break;
                case short sh: mined.Ints.Add(sh); break;
                case byte b: mined.Ints.Add(b); break;
                case decimal d: mined.Decimals.Add(d); break;
                case double db: mined.Doubles.Add(db); break;
                case float f: mined.Doubles.Add(f); break;
            }
        }

        foreach (var node in body.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            switch (node)
            {
                case BinaryExpressionSyntax be when IsComparison(be.Kind()):
                    Take(be.Left); Take(be.Right);
                    break;
                case SimpleCaseClauseSyntax simple:        // Case "NO"
                    Take(simple.Value);
                    break;
                case RangeCaseClauseSyntax range:          // Case 1 To 10
                    Take(range.LowerBound); Take(range.UpperBound);
                    break;
                case RelationalCaseClauseSyntax rel:       // Case Is > 10000
                    Take(rel.Value);
                    break;
                case InvocationExpressionSyntax inv        // s.StartsWith("CLEARANCE"), coupon.Contains("SAVE")
                    when inv.Expression is MemberAccessExpressionSyntax ma && IsPredicateCall(ma.Name.Identifier.ValueText):
                    if (inv.ArgumentList is not null)
                        foreach (var a in inv.ArgumentList.Arguments)
                            Take(a.GetExpression());
                    break;
            }
        }
        return mined;
    }

    private static bool IsPredicateCall(string name) => name is
        "StartsWith" or "EndsWith" or "Contains" or "Equals" or "IndexOf" or "IsMatch" or "Match";

    private static bool IsComparison(VbKind kind) => kind is
        VbKind.LessThanExpression or VbKind.GreaterThanExpression
        or VbKind.LessThanOrEqualExpression or VbKind.GreaterThanOrEqualExpression
        or VbKind.EqualsExpression or VbKind.NotEqualsExpression;
}
