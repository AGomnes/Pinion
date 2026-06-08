using System.Text.RegularExpressions;
using Pinion.Engine.Model;

namespace Pinion.Generate;

/// <summary>
/// Orchestrates the generate pipeline for one target, end to end:
/// extract context → scrub → LLM → emit → compile + run → (repair loop) → golden master.
/// The compile-execute-repair loop is what separates a reliable tool from a demo.
/// </summary>
public sealed class TestGenerator
{
    private readonly IGenerationAdapter _adapter;
    private readonly ILlmClient _llm;
    private readonly GenerationOptions _options;
    private readonly Action<string>? _log;

    private readonly UsageMeter? _meter;
    private readonly decimal? _maxSpendUsd;

    public TestGenerator(IGenerationAdapter adapter, ILlmClient llm, GenerationOptions? options = null,
        Action<string>? log = null, UsageMeter? meter = null, decimal? maxSpendUsd = null)
    {
        _adapter = adapter;
        _llm = llm;
        _options = options ?? GenerationOptions.Default;
        _log = log;
        _meter = meter;
        _maxSpendUsd = maxSpendUsd;
    }

    /// <summary>True once a spend ceiling is set and cumulative estimated cost has reached it.</summary>
    public bool SpendCeilingReached =>
        _maxSpendUsd is { } cap && _meter is { } m && m.EstimatedCostUsd >= cap;

    public async Task<GenerationResult> GenerateAsync(CodeUnit unit, CancellationToken ct)
    {
        var context = await _adapter.ExtractContextAsync(unit, ct).ConfigureAwait(false);

        // Scrub everything that would leave the machine. The system prompt is static, but
        // scrub it too so the dry-run output is the literal outbound payload.
        var scrubbedSystem = SecretScrubber.Scrub(PromptBuilder.System());
        var scrubbedUser = SecretScrubber.Scrub(PromptBuilder.InitialUser(context));
        if (scrubbedUser.Redactions > 0)
            _log?.Invoke($"[scrub] redacted {scrubbedUser.Redactions} secret(s) from {unit.DisplayName} before send.");

        var messages = new List<LlmMessage> { LlmMessage.User(scrubbedUser.Text) };

        if (_options.DryRun)
        {
            var preview = new LlmRequest(_options.Model, scrubbedSystem.Text, messages, _options.MaxTokens);
            _log?.Invoke($"[dry-run] {unit.DisplayName}: the following bytes would be sent to the model (and nothing else):");
            _log?.Invoke(AnthropicClient.BuildRequestJson(preview));
            return new GenerationResult(unit, Success: false, Attempts: 0, TestFilePath: null, SnapshotPath: null,
                Diagnostics: new[] { "dry-run: no request sent" });
        }

        var lastDiagnostics = Array.Empty<string>() as IReadOnlyList<string>;
        int maxTries = Math.Max(1, _options.MaxRepairAttempts + 1);

        for (int attempt = 1; attempt <= maxTries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Hard cost guard: never start another paid call once the ceiling is hit.
            if (SpendCeilingReached)
            {
                _log?.Invoke($"[budget] spend ceiling ${_maxSpendUsd:0.00} reached — stopping before {unit.DisplayName}.");
                return new GenerationResult(unit, Success: false, Attempts: attempt - 1, TestFilePath: null,
                    SnapshotPath: null, Diagnostics: new[] { $"stopped: spend ceiling ${_maxSpendUsd:0.00} reached" });
            }

            _log?.Invoke($"[generate] {unit.DisplayName}: attempt {attempt}/{maxTries} via {_llm.Name} ({_options.Model}).");

            // Snapshot the conversation so each request is independent of later repair turns.
            var request = new LlmRequest(_options.Model, scrubbedSystem.Text, messages.ToList(), _options.MaxTokens);
            var response = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
            _meter?.Add(_options.Model, response.Usage);

            string body = ExtractCode(response.Text);
            var emitted = await _adapter.EmitTestAsync(unit, body, ct).ConfigureAwait(false);
            var exec = await _adapter.RunAndSnapshotAsync(emitted, ct).ConfigureAwait(false);

            if (exec.Compiled && exec.Passed)
            {
                _log?.Invoke($"[generate] {unit.DisplayName}: golden master captured → {exec.SnapshotPath}");
                return new GenerationResult(unit, Success: true, Attempts: attempt, TestFilePath: emitted.FilePath,
                    SnapshotPath: exec.SnapshotPath, Diagnostics: exec.Diagnostics);
            }

            lastDiagnostics = exec.Diagnostics;
            _log?.Invoke($"[generate] {unit.DisplayName}: attempt {attempt} failed ({exec.Diagnostics.Count} diagnostic(s)).");

            // Feed the error back for repair (unless we're out of tries).
            if (attempt < maxTries)
            {
                messages.Add(LlmMessage.Assistant(response.Text));
                messages.Add(LlmMessage.User(SecretScrubber.Scrub(PromptBuilder.Repair(exec.Diagnostics)).Text));
            }
        }

        return new GenerationResult(unit, Success: false, Attempts: maxTries, TestFilePath: null, SnapshotPath: null,
            Diagnostics: lastDiagnostics);
    }

    /// <summary>
    /// Pull the C# file out of a model reply: prefer a fenced ```csharp block; otherwise
    /// drop any leading prose ("Here is the test:") before the first real code line. Real
    /// models do both, so this keeps the emitted file clean before it hits the compiler.
    /// </summary>
    internal static string ExtractCode(string text)
    {
        var fenced = Regex.Match(text, "```(?:csharp|cs)?\\s*\\n(?<code>[\\s\\S]*?)```", RegexOptions.IgnoreCase);
        if (fenced.Success) return fenced.Groups["code"].Value.Trim();

        // No fence — skip prose lines until the first that looks like the start of a C# file.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, IsCodeStart);
        return (start <= 0 ? text : string.Join("\n", lines[start..])).Trim();
    }

    private static bool IsCodeStart(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith("using ") || t.StartsWith("namespace ") || t.StartsWith("//")
            || t.StartsWith("#") || t.StartsWith("[") || t.StartsWith("public ")
            || t.StartsWith("internal ") || t.StartsWith("sealed ") || t.StartsWith("static ")
            || t.StartsWith("class ");
    }
}
