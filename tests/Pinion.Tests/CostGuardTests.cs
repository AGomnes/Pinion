using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class ModelPricingTests
{
    [Theory]
    [InlineData("claude-haiku-4-5", 1, 5)]
    [InlineData("claude-sonnet-4-6", 3, 15)]
    [InlineData("claude-opus-4-8", 5, 25)]
    [InlineData("something-unknown", 3, 15)]
    public void Rates_map_by_model_family(string model, int inRate, int outRate)
    {
        var r = ModelPricing.For(model);
        Assert.Equal(inRate, r.InputPerMTok);
        Assert.Equal(outRate, r.OutputPerMTok);
    }

    [Fact]
    public void Cost_is_input_plus_output_priced_per_million()
    {
        var cost = ModelPricing.CostUsd("claude-sonnet-4-6", new LlmUsage(InputTokens: 1_000_000, OutputTokens: 1_000_000));
        Assert.Equal(18m, cost);
    }

    [Fact]
    public void Meter_accumulates_across_calls()
    {
        var meter = new UsageMeter();
        meter.Add("claude-sonnet-4-6", new LlmUsage(InputTokens: 1_000_000));
        meter.Add("claude-sonnet-4-6", new LlmUsage(OutputTokens: 1_000_000));
        Assert.Equal(2, meter.Calls);
        Assert.Equal(18m, meter.EstimatedCostUsd);
    }
}

public class SpendCeilingTests
{
    private static CodeUnit Unit() => new(
        "N.C.M()", "C.M", "C.cs", 1, 5, "void M()", System.Array.Empty<ParamInfo>(), "void",
        1, 5, System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>(),
        false, true, System.Array.Empty<string>());

    private sealed class CostlyLlm : ILlmClient
    {
        public int Calls { get; private set; }
        public string Name => "costly";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse("// code", new LlmUsage(InputTokens: 1_000_000, OutputTokens: 1_000_000)));
        }
    }

    private sealed class AlwaysFailsAdapter : IGenerationAdapter
    {
        public Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct) =>
            Task.FromResult(new GenerationContext(unit, "N", "C", "class C {}", System.Array.Empty<string>()));
        public Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string body, CancellationToken ct) =>
            Task.FromResult(new GeneratedTest(unit, "T", body, "T.cs"));
        public Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct) =>
            Task.FromResult(new ExecutionResult(false, false, new[] { "error CS9999" }, null));
    }

    [Fact]
    public async Task Ceiling_stops_the_repair_loop_before_the_next_paid_call()
    {
        var llm = new CostlyLlm();
        var meter = new UsageMeter();
        var gen = new TestGenerator(new AlwaysFailsAdapter(), llm, options: null, log: null, meter, maxSpendUsd: 1.00m);

        var result = await gen.GenerateAsync(Unit(), default);

        Assert.False(result.Success);
        Assert.Equal(1, llm.Calls);
        Assert.True(gen.SpendCeilingReached);
        Assert.Contains(result.Diagnostics, d => d.Contains("spend ceiling"));
    }
}
