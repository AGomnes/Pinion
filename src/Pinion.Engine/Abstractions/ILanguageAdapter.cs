using Pinion.Engine.Model;

namespace Pinion.Engine.Abstractions;

/// <summary>
/// The one boundary between the language-agnostic engine and a specific language's
/// tooling for the free `analyze` tier. The engine talks ONLY to this interface and to
/// the IR it returns — never to Roslyn / ts-morph / ast types directly.
/// </summary>
/// <remarks>
/// This is the ANALYZE surface. The `generate` surface is a separate interface
/// (<c>Pinion.Generate.IGenerationAdapter</c>) in the generate assembly, so the analyze engine
/// has no compile-time dependency on the generation code.
/// </remarks>
public interface ILanguageAdapter
{
    /// <summary>Language id, e.g. "csharp".</summary>
    string Language { get; }

    /// <summary>
    /// Analyze a project or solution and return the IR for every code unit found.
    /// Free tier — must work with no AI involved.
    /// </summary>
    Task<IReadOnlyList<CodeUnit>> AnalyzeAsync(string projectOrSolutionPath, CancellationToken ct);
}
