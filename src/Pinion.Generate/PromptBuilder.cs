using System.Text;

namespace Pinion.Generate;

/// <summary>
/// Builds the prompts for the generate pipeline. The system prompt is stable (so it
/// caches) and encodes the one rule that must never be wrong: we RECORD what the code
/// does, we do NOT assert what it should do.
/// </summary>
public static class PromptBuilder
{
    /// <summary>The cacheable, project-wide instruction prefix. Identical across all targets.</summary>
    public static string System() =>
        """
        You write CHARACTERIZATION tests (a.k.a. pinning tests) for legacy C# code.

        The single most important rule — do not get this wrong:
        - DO NOT assert what the method SHOULD return. You do not know the intended behavior.
        - You RECORD what the method ACTUALLY does today (bugs included). The snapshot IS the assertion.

        Output requirements:
        - Produce ONE self-contained xUnit test file using Verify for the snapshot.
        - Compile against xunit + VerifyXunit. Use `using VerifyXunit;`, `using Xunit;`,
          and `using static VerifyXunit.Verifier;`. Put the class in `namespace Pinion.Generated;`.
        - Class name MUST be unique per method: `<Type>_<Method>_CharacterizationTests`.
        - Exactly one test: `[Fact] public async Task <Method>_characterization()`.
        - Inside it, call the target across a DIVERSE set of inputs: typical values, edge
          cases, boundaries, and (where the parameter allows) null/empty/zero/negative.
        - For EACH input, wrap the call in try/catch and capture the OUTCOME — either the
          returned value or, if it throws, `ex.GetType().Name + ": " + ex.Message` — as one
          entry `new { Input = <description>, Outcome = (object?)<value> }` in a `List<object>`.
        - Finish with: `await Verify(entries);`
        - Never write Assert.Equal / Assert.True against an expected value. The only
          verification is the Verify(...) snapshot call.
        - Reference the real type from its namespace. Construct it as needed.
        - Output ONLY the raw C# file content. No markdown fences, no commentary.
        """;

    /// <summary>The per-target user turn: the method under characterization and its context.</summary>
    public static string InitialUser(GenerationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Namespace: {ctx.Namespace}");
        sb.AppendLine($"Type: {ctx.ContainingType}");
        sb.AppendLine($"Target method: {ctx.Unit.Signature}");
        sb.AppendLine();

        if (ctx.CalleeSignatures.Count > 0)
        {
            sb.AppendLine("Signatures it calls (for context):");
            foreach (var c in ctx.CalleeSignatures) sb.AppendLine($"  {c}");
            sb.AppendLine();
        }

        sb.AppendLine("Source of the type (the method to characterize is within it):");
        sb.AppendLine("```csharp");
        sb.AppendLine(ctx.SourceCode);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"Generate the characterization test for {ctx.Unit.DisplayName}. Output only the C# file.");
        return sb.ToString();
    }

    /// <summary>
    /// The repair turn: hand the failure back and ask for a fix. Only COMPILER errors (which reference
    /// code, and which the model needs to fix the file) are echoed verbatim. Runtime output is NOT sent
    /// back — a captured return value or exception message can contain real data from executing the
    /// target, and this is an outbound channel; for a pure runtime failure we describe the shape only.
    /// </summary>
    public static string Repair(IReadOnlyList<string> diagnostics)
    {
        var compilerErrors = diagnostics
            .Where(d => d.Contains("error CS", StringComparison.Ordinal))
            .Take(25)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("The test you produced did not compile/run. Fix it and output the full corrected C# file (only the code).");
        if (compilerErrors.Count > 0)
        {
            sb.AppendLine("Compiler errors:");
            foreach (var d in compilerErrors) sb.AppendLine($"  {d}");
        }
        else
        {
            sb.AppendLine("It compiled but failed at runtime (an unexpected exception, or a snapshot it could " +
                "not capture). Ensure every call is wrapped in try/catch and the outcome is recorded as text — " +
                "never assert an expected value. (Runtime output is withheld here as it can contain real data.)");
        }
        return sb.ToString();
    }
}
