using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class RepairLoopTests
{
    private static CodeUnit Unit() => new(
        "N.C.M()", "C.M", "C.cs", 1, 5, "void M()", Array.Empty<ParamInfo>(), "void",
        1, 5, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        false, true, Array.Empty<string>());

    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly Queue<string> _responses;
        public List<LlmRequest> Requests { get; } = new();
        public ScriptedLlm(params string[] responses) => _responses = new Queue<string>(responses);
        public string Name => "scripted";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new LlmResponse(_responses.Dequeue(), new LlmUsage()));
        }
    }

    private sealed class ScriptedAdapter : IGenerationAdapter
    {
        private readonly Queue<ExecutionResult> _outcomes;
        public List<string> EmittedBodies { get; } = new();
        public ScriptedAdapter(params ExecutionResult[] outcomes) => _outcomes = new Queue<ExecutionResult>(outcomes);

        public Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct) =>
            Task.FromResult(new GenerationContext(unit, "N", "C", "class C {}", Array.Empty<string>()));

        public Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string body, CancellationToken ct)
        {
            EmittedBodies.Add(body);
            return Task.FromResult(new GeneratedTest(unit, "C_M_Tests", body, "C_M_Tests.cs"));
        }

        public Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct) =>
            Task.FromResult(_outcomes.Dequeue());
    }

    [Fact]
    public async Task Failed_first_attempt_is_repaired_and_succeeds_on_the_second()
    {
        var llm = new ScriptedLlm("// broken test", "// fixed test");
        var adapter = new ScriptedAdapter(
            new ExecutionResult(Compiled: false, Passed: false, new[] { "error CS1002: ; expected" }, null),
            new ExecutionResult(Compiled: true, Passed: true, Array.Empty<string>(), "C_M_Tests.M_characterization.verified.txt"));

        var result = await new TestGenerator(adapter, llm).GenerateAsync(Unit(), default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.NotNull(result.SnapshotPath);

        Assert.Equal(2, llm.Requests.Count);
        Assert.True(llm.Requests[1].Messages.Count > llm.Requests[0].Messages.Count);
        Assert.Contains(llm.Requests[1].Messages, m => m.Content.Contains("error CS1002"));
    }

    [Fact]
    public async Task Gives_up_after_max_repairs_and_reports_diagnostics()
    {
        var llm = new ScriptedLlm("bad1", "bad2");
        var adapter = new ScriptedAdapter(
            new ExecutionResult(false, false, new[] { "error CS0103: undefined" }, null),
            new ExecutionResult(false, false, new[] { "error CS0103: still undefined" }, null));

        var options = GenerationOptions.Default with { MaxRepairAttempts = 1 };
        var result = await new TestGenerator(adapter, llm, options).GenerateAsync(Unit(), default);

        Assert.False(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Contains(result.Diagnostics, d => d.Contains("CS0103"));
    }
}
