using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Pinion.Adapters.CSharp;

/// <summary>The ambient reads `pinion seam` can mechanically fix. Deliberately ONLY the pure-value
/// ambients (time, identity) — resource obstacles (File, HttpClient, SqlConnection) need a designed
/// abstraction, which is a human decision, so they stay flagged as manual in the report.</summary>
internal enum AmbientKind
{
    DateTimeNow, DateTimeUtcNow, DateTimeToday,
    DateTimeOffsetNow, DateTimeOffsetUtcNow,
    GuidNewGuid,
}

/// <summary>
/// The `pinion seam` transform: for a method whose body reads ambient values (<c>DateTime.Now</c>,
/// <c>Guid.NewGuid()</c>, …), introduce a Feathers seam as an OVERLOAD —
/// <code>
///   // original signature becomes a delegating wrapper (source- and binary-compatible):
///   public string Stamp(string a) => Stamp(a, DateTime.Now);
///   // the real body moves to an overload whose ambient values are parameters (deterministic, lockable):
///   public string Stamp(string a, global::System.DateTime now) => now.ToString("o") + a;
/// </code>
/// Behavior-preserving by construction: existing callers hit the wrapper, which supplies the exact
/// original expressions. The overload is what `generate` can then lock with fixed inputs.
/// Detection is symbol-based (System.DateTime/System.Guid only) and stays in lockstep with
/// <see cref="SeamAnalyzer"/>'s obstacle vocabulary (guarded by a test).
/// </summary>
internal static class SeamRewriter
{
    internal sealed record Spec(string Display, string ParamName, string ParamType);

    internal static readonly IReadOnlyDictionary<AmbientKind, Spec> Specs = new Dictionary<AmbientKind, Spec>
    {
        [AmbientKind.DateTimeNow] = new("DateTime.Now", "now", "global::System.DateTime"),
        [AmbientKind.DateTimeUtcNow] = new("DateTime.UtcNow", "utcNow", "global::System.DateTime"),
        [AmbientKind.DateTimeToday] = new("DateTime.Today", "today", "global::System.DateTime"),
        [AmbientKind.DateTimeOffsetNow] = new("DateTimeOffset.Now", "offsetNow", "global::System.DateTimeOffset"),
        [AmbientKind.DateTimeOffsetUtcNow] = new("DateTimeOffset.UtcNow", "offsetUtcNow", "global::System.DateTimeOffset"),
        [AmbientKind.GuidNewGuid] = new("Guid.NewGuid", "newGuid", "global::System.Guid"),
    };

    internal sealed record AmbientRead(SyntaxNode Node, AmbientKind Kind);

    /// <summary>One method the rewrite seamed: its name plus the ambient reads that became parameters.</summary>
    internal sealed record Seamed(string Method, IReadOnlyList<string> Reads);

    private const string ReadAnnKind = "pinion-seam-read";
    private static readonly SyntaxAnnotation MethodAnn = new("pinion-seam-method");


    /// <summary>Symbol-resolved ambient reads inside a body. Conservative: static members of
    /// System.DateTime/System.DateTimeOffset/System.Guid only, and never inside nameof(…).</summary>
    internal static IReadOnlyList<AmbientRead> FindSeamableReads(SyntaxNode body, SemanticModel model, CancellationToken ct)
    {
        var reads = new List<AmbientRead>();
        foreach (var node in body.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            AmbientKind? kind = node switch
            {
                MemberAccessExpressionSyntax ma when model.GetSymbolInfo(ma, ct).Symbol is IPropertySymbol p => ClassifyProperty(p),
                InvocationExpressionSyntax inv when model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol m => ClassifyMethod(m),
                _ => null,
            };
            if (kind is { } k && !InsideNameOf(node)) reads.Add(new AmbientRead(node, k));
        }
        return reads;
    }

    private static AmbientKind? ClassifyProperty(IPropertySymbol p)
    {
        if (!p.IsStatic) return null;
        return (Full(p.ContainingType), p.Name) switch
        {
            ("System.DateTime", "Now") => AmbientKind.DateTimeNow,
            ("System.DateTime", "UtcNow") => AmbientKind.DateTimeUtcNow,
            ("System.DateTime", "Today") => AmbientKind.DateTimeToday,
            ("System.DateTimeOffset", "Now") => AmbientKind.DateTimeOffsetNow,
            ("System.DateTimeOffset", "UtcNow") => AmbientKind.DateTimeOffsetUtcNow,
            _ => null,
        };
    }

    private static AmbientKind? ClassifyMethod(IMethodSymbol m) =>
        m.IsStatic && m.Name == "NewGuid" && Full(m.ContainingType) == "System.Guid"
            ? AmbientKind.GuidNewGuid
            : null;

    private static string Full(INamedTypeSymbol? t) => t?.ToDisplayString() ?? "";

    private static bool InsideNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });


    /// <summary>
    /// Rewrite every eligible method in a document (optionally filtered): each becomes a delegating
    /// wrapper + a seam overload. Pure function of the tree — returns the new root plus what was seamed
    /// and what was skipped (with a human reason). The document text is otherwise untouched.
    /// </summary>
    internal static (SyntaxNode NewRoot, IReadOnlyList<Seamed> SeamedMethods, IReadOnlyList<(string Method, string Reason)> Skipped)
        RewriteDocument(SyntaxNode root, SemanticModel model, Func<MethodDeclarationSyntax, bool>? include, CancellationToken ct)
    {
        var seamedInfo = new List<Seamed>();
        var skipped = new List<(string, string)>();
        var candidates = new List<(MethodDeclarationSyntax Method, List<AmbientRead> Reads)>();

        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (include is not null && !include(m)) continue;
            SyntaxNode? body = (SyntaxNode?)m.Body ?? m.ExpressionBody;
            if (body is null) continue;

            var reads = FindSeamableReads(body, model, ct).ToList();
            if (reads.Count == 0) continue;

            if (Ineligible(m, body, reads) is { } reason) { skipped.Add((MethodLabel(m), reason)); continue; }
            candidates.Add((m, reads));
            seamedInfo.Add(new Seamed(MethodLabel(m), reads.Select(r => Specs[r.Kind].Display).Distinct().ToList()));
        }

        if (candidates.Count == 0) return (root, seamedInfo, skipped);

        var kindByNode = candidates.SelectMany(c => c.Reads).ToDictionary(r => r.Node, r => r.Kind);
        var toAnnotate = kindByNode.Keys.Concat(candidates.Select(c => (SyntaxNode)c.Method));
        var newRoot = root.ReplaceNodes(toAnnotate, (orig, rewritten) =>
            kindByNode.TryGetValue(orig, out var k)
                ? rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(ReadAnnKind, k.ToString()))
                : rewritten.WithAdditionalAnnotations(MethodAnn));

        while (newRoot.GetAnnotatedNodes(MethodAnn).OfType<MethodDeclarationSyntax>().FirstOrDefault() is { } m)
            newRoot = newRoot.ReplaceNode(m, TransformMethod(m));

        return (newRoot, seamedInfo, skipped);
    }

    /// <summary>Why a method with ambient reads can't be auto-seamed (null = eligible).</summary>
    private static string? Ineligible(MethodDeclarationSyntax m, SyntaxNode body, List<AmbientRead> reads)
    {
        if (IsDelegatingWrapper(m)) return "already seamed (delegating wrapper)";
        if (m.TypeParameterList is not null) return "generic method — introduce the seam manually";
        if (m.ExplicitInterfaceSpecifier is not null) return "explicit interface implementation — seam manually";
        foreach (var mod in m.Modifiers)
            if (mod.IsKind(SyntaxKind.PartialKeyword) || mod.IsKind(SyntaxKind.AbstractKeyword) || mod.IsKind(SyntaxKind.ExternKeyword))
                return $"{mod.ValueText} method — seam manually";
        if (ResolveParamNames(m, body, reads.Select(r => r.Kind).Distinct()) is null)
            return "couldn't pick collision-free parameter names — seam manually";
        return null;
    }

    private static bool IsDelegatingWrapper(MethodDeclarationSyntax m)
    {
        ExpressionSyntax? expr = m.ExpressionBody?.Expression;
        if (expr is null && m.Body is { Statements: [ExpressionStatementSyntax single] }) expr = single.Expression;
        if (expr is null && m.Body is { Statements: [ReturnStatementSyntax ret] }) expr = ret.Expression;
        return expr is InvocationExpressionSyntax { Expression: IdentifierNameSyntax id }
            && id.Identifier.ValueText == m.Identifier.ValueText;
    }

    /// <summary>Collision-free parameter names for the needed ambient kinds, or null when impossible.
    /// Deterministic, so the eligibility check and the transform always agree.</summary>
    private static Dictionary<AmbientKind, string>? ResolveParamNames(
        MethodDeclarationSyntax m, SyntaxNode body, IEnumerable<AmbientKind> kinds)
    {
        var used = m.ParameterList.Parameters.Select(p => p.Identifier.ValueText)
            .Concat(body.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText))
            .ToHashSet(StringComparer.Ordinal);

        var names = new Dictionary<AmbientKind, string>();
        foreach (var kind in kinds)
        {
            string name = Specs[kind].ParamName;
            if (used.Contains(name)) name += "Seam";
            if (used.Contains(name)) return null;
            used.Add(name);
            names[kind] = name;
        }
        return names;
    }

    /// <summary>The wrapper + seam-overload pair replacing one annotated method.</summary>
    private static MemberDeclarationSyntax[] TransformMethod(MethodDeclarationSyntax m)
    {
        var readNodes = m.GetAnnotatedNodes(ReadAnnKind).ToList();
        var kindOf = readNodes.ToDictionary(n => n, n => Enum.Parse<AmbientKind>(n.GetAnnotations(ReadAnnKind).First().Data!));
        var kindsInOrder = readNodes.OrderBy(n => n.SpanStart).Select(n => kindOf[n]).Distinct().ToList();
        SyntaxNode body = (SyntaxNode?)m.Body ?? m.ExpressionBody!;
        var names = ResolveParamNames(m, body, kindsInOrder)!;

        int insertAt = 0;
        while (insertAt < m.ParameterList.Parameters.Count
               && m.ParameterList.Parameters[insertAt].Default is null
               && !m.ParameterList.Parameters[insertAt].Modifiers.Any(SyntaxKind.ParamsKeyword))
            insertAt++;

        var overload = m.ReplaceNodes(readNodes,
            (orig, _) => IdentifierName(names[kindOf[orig]]).WithTriviaFrom(orig));
        var newParams = kindsInOrder.Select((k, i) =>
        {
            var p = Parameter(Identifier(names[k]))
                .WithType(ParseTypeName(Specs[k].ParamType).WithTrailingTrivia(Space));
            return insertAt > 0 || i > 0 ? p.WithLeadingTrivia(Space) : p;
        }).ToList();

        var allParams = m.ParameterList.Parameters.ToList();
        allParams.InsertRange(insertAt, newParams);
        int after = insertAt + newParams.Count;
        if (after < allParams.Count && !allParams[after].GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)))
            allParams[after] = allParams[after].WithLeadingTrivia(Space);
        var indent = m.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var overloadLead = new List<SyntaxTrivia> { EndOfLine("\n") };
        if (indent.IsKind(SyntaxKind.WhitespaceTrivia)) overloadLead.Add(indent);
        overloadLead.Add(Comment("// Seam (pinion): same behavior; ambient values are parameters, so this overload is deterministic and lockable."));
        overloadLead.Add(EndOfLine("\n"));
        if (indent.IsKind(SyntaxKind.WhitespaceTrivia)) overloadLead.Add(indent);

        overload = overload
            .WithoutAnnotations(MethodAnn)
            .WithAttributeLists(default)
            .WithModifiers(TokenList(overload.Modifiers.Where(t =>
                !t.IsKind(SyntaxKind.OverrideKeyword) && !t.IsKind(SyntaxKind.VirtualKeyword)
                && !t.IsKind(SyntaxKind.SealedKeyword) && !t.IsKind(SyntaxKind.NewKeyword))))
            .WithParameterList(m.ParameterList.WithParameters(SeparatedList(allParams)))
            .WithLeadingTrivia(overloadLead)
            .WithTrailingTrivia(m.GetTrailingTrivia());

        var forwarded = m.ParameterList.Parameters.Select(p =>
        {
            var arg = Argument(IdentifierName(p.Identifier.ValueText));
            if (p.Modifiers.Any(SyntaxKind.RefKeyword)) arg = arg.WithRefKindKeyword(Token(SyntaxKind.RefKeyword));
            if (p.Modifiers.Any(SyntaxKind.OutKeyword)) arg = arg.WithRefKindKeyword(Token(SyntaxKind.OutKeyword));
            return arg;
        }).ToList();
        var defaults = kindsInOrder.Select(k =>
        {
            var firstNode = readNodes.Where(n => kindOf[n] == k).OrderBy(n => n.SpanStart).First();
            return Argument(ParseExpression(firstNode.ToString()));
        });
        var callArgs = forwarded.ToList();
        callArgs.InsertRange(insertAt, defaults);
        var call = InvocationExpression(
            IdentifierName(m.Identifier.ValueText),
            ArgumentList(SeparatedList(callArgs)));

        var wrapper = m
            .WithoutAnnotations(MethodAnn)
            .WithModifiers(TokenList(m.Modifiers.Where(t => !t.IsKind(SyntaxKind.AsyncKeyword))))
            .WithParameterList(m.ParameterList.WithoutTrailingTrivia())
            .WithBody(null)
            .WithExpressionBody(ArrowExpressionClause(call).NormalizeWhitespace().WithLeadingTrivia(Space))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .WithTrailingTrivia(EndOfLine("\n"));

        return new MemberDeclarationSyntax[] { wrapper, overload };
    }

    private static string MethodLabel(MethodDeclarationSyntax m)
    {
        string type = m.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "";
        return type.Length > 0 ? $"{type}.{m.Identifier.ValueText}" : m.Identifier.ValueText;
    }
}
