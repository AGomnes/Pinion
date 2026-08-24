using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// The never-send guarantee: when a unit's file/namespace matches a no-send rule, TestGenerator must
/// refuse the AI path and make ZERO outbound calls — not even extract context. This is the spec's
/// security boundary, enforced at the pipeline (not just the CLI), so any caller is bound by it.
/// </summary>
public class NeverSendTests
{
    private static CodeUnit Unit() => new(
        "MyCompany.Secrets.Vault.Decrypt(string)", "Vault.Decrypt", "src/Secrets/Vault.cs", 1, 5,
        "public string Decrypt(string cipher)", System.Array.Empty<ParamInfo>(), "string",
        1, 5, System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>(),
        false, true, System.Array.Empty<string>());

    private sealed class TripwireLlm : ILlmClient
    {
        public string Name => "tripwire";
        public string PreviewRequest(LlmRequest request) => "(test double)";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("outbound call made for a never-send unit");
    }

    private sealed class TripwireAdapter : IGenerationAdapter
    {
        public Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("context extracted for a never-send unit (its source was read for sending)");
        public Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string body, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("emit called for a never-send unit");
        public Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("run called for a never-send unit");
    }

    [Fact]
    public async Task Never_send_unit_makes_no_outbound_call_and_is_refused()
    {
        var gen = new TestGenerator(new TripwireAdapter(), new TripwireLlm(),
            neverSend: new[] { "*/Secrets/*" });

        var result = await gen.GenerateAsync(Unit(), default);

        Assert.False(result.Success);
        Assert.Equal(0, result.Attempts);
        Assert.Contains(result.Diagnostics, d => d.Contains("never-send"));
    }

    [Fact]
    public async Task Never_send_is_enforced_even_in_dry_run()
    {
        var options = GenerationOptions.Default with { DryRun = true };
        var gen = new TestGenerator(new TripwireAdapter(), new TripwireLlm(), options,
            neverSend: new[] { "MyCompany.Secrets" });

        var result = await gen.GenerateAsync(Unit(), default);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Contains("never-send"));
    }

    [Fact]
    public async Task Unmatched_unit_is_sent_normally()
    {
        var llm = new RecordingLlm("class T {}");
        var adapter = new PassThroughAdapter();
        var gen = new TestGenerator(adapter, llm, neverSend: new[] { "*/Secrets/*" });

        var result = await gen.GenerateAsync(
            new CodeUnit("MyCompany.Public.Calc.Add(int,int)", "Calc.Add", "src/Public/Calc.cs", 1, 5,
                "public int Add(int a, int b)", System.Array.Empty<ParamInfo>(), "int", 1, 5,
                System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>(),
                false, true, System.Array.Empty<string>()),
            default);

        Assert.True(result.Success);
        Assert.Equal(1, llm.Calls);
    }

    private sealed class RecordingLlm : ILlmClient
    {
        private readonly string _response;
        public int Calls { get; private set; }
        public RecordingLlm(string response) => _response = response;
        public string Name => "recording";
        public string PreviewRequest(LlmRequest request) => "(test double)";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse(_response, new LlmUsage()));
        }
    }

    private sealed class PassThroughAdapter : IGenerationAdapter
    {
        public Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct) =>
            Task.FromResult(new GenerationContext(unit, "N", "C", "class C {}", System.Array.Empty<string>()));
        public Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string body, CancellationToken ct) =>
            Task.FromResult(new GeneratedTest(unit, "C_Tests", body, "C_Tests.cs"));
        public Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct) =>
            Task.FromResult(new ExecutionResult(Compiled: true, Passed: true, System.Array.Empty<string>(), "C_Tests.verified.txt"));
    }
}
