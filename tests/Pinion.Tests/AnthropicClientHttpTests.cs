using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// Exercises the REAL <see cref="AnthropicClient"/> over HTTP against <see cref="MockAnthropicServer"/>:
/// header/serialization correctness, response + usage parsing, error mapping, and the full
/// compile→run→repair loop — all with no API key and no cost.
/// </summary>
public class AnthropicClientHttpTests
{
    private static LlmRequest Req() =>
        new("claude-sonnet-4-6", "SYSTEM", new[] { LlmMessage.User("write a test") }, 4096);

    [Fact]
    public async Task Sends_required_headers_to_the_messages_endpoint_and_parses_the_reply()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueMessage("```csharp\nclass T {}\n```", inputTokens: 11, outputTokens: 22, cacheRead: 7, cacheCreation: 3);

        using var client = new AnthropicClient("test-key", server.BaseUrl);
        var resp = await client.CompleteAsync(Req(), default);

        var sent = Assert.Single(server.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/messages", sent.Path);
        Assert.Equal("test-key", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.Contains("\"model\":\"claude-sonnet-4-6\"", sent.Body);
        Assert.Contains("write a test", sent.Body);

        Assert.Contains("class T {}", resp.Text);
        Assert.Equal(11, resp.Usage.InputTokens);
        Assert.Equal(22, resp.Usage.OutputTokens);
        Assert.Equal(7, resp.Usage.CacheReadInputTokens);
        Assert.Equal(3, resp.Usage.CacheCreationInputTokens);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(401, true)]
    [InlineData(500, false)]
    public async Task Error_status_becomes_LlmApiException_with_correct_abort_flag(int status, bool shouldAbort)
    {
        using var server = new MockAnthropicServer();
        for (int i = 0; i < 6; i++) server.EnqueueError(status, message: "nope");

        using var client = new AnthropicClient("k", server.BaseUrl) { MaxRetries = 2, RetryBaseDelay = TimeSpan.Zero };

        var ex = await Assert.ThrowsAsync<LlmApiException>(() => client.CompleteAsync(Req(), default));
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(shouldAbort, ex.ShouldAbortRun);
    }

    [Fact]
    public async Task Transient_error_is_retried_then_succeeds()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueError(503);
        server.EnqueueMessage("```csharp\nclass T {}\n```");

        using var client = new AnthropicClient("k", server.BaseUrl) { RetryBaseDelay = TimeSpan.Zero };
        var resp = await client.CompleteAsync(Req(), default);

        Assert.Contains("class T {}", resp.Text);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Non_retryable_4xx_aborts_on_the_first_attempt()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueError(400, message: "bad request");

        using var client = new AnthropicClient("k", server.BaseUrl) { RetryBaseDelay = TimeSpan.Zero };
        await Assert.ThrowsAsync<LlmApiException>(() => client.CompleteAsync(Req(), default));

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Compile_run_repair_loop_works_over_real_http()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueMessage("```csharp\n// broken\n```");
        server.EnqueueMessage("```csharp\n// fixed\n```");

        using var client = new AnthropicClient("k", server.BaseUrl);
        var adapter = new ScriptedAdapter(
            new ExecutionResult(Compiled: false, Passed: false, new[] { "error CS1002: ; expected" }, null),
            new ExecutionResult(Compiled: true, Passed: true, System.Array.Empty<string>(), "T.verified.txt"));

        var result = await new TestGenerator(adapter, client).GenerateAsync(Unit(), default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("error CS1002", server.Requests[1].Body);
    }

    private static CodeUnit Unit() => new(
        "N.C.M()", "C.M", "C.cs", 1, 5, "void M()", System.Array.Empty<ParamInfo>(), "void",
        1, 5, System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>(),
        false, true, System.Array.Empty<string>());

    private sealed class ScriptedAdapter : IGenerationAdapter
    {
        private readonly Queue<ExecutionResult> _outcomes;
        public ScriptedAdapter(params ExecutionResult[] outcomes) => _outcomes = new Queue<ExecutionResult>(outcomes);

        public Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct) =>
            Task.FromResult(new GenerationContext(unit, "N", "C", "class C {}", System.Array.Empty<string>()));
        public Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string body, CancellationToken ct) =>
            Task.FromResult(new GeneratedTest(unit, "C_M_Tests", body, "C_M_Tests.cs"));
        public Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct) =>
            Task.FromResult(_outcomes.Dequeue());
    }
}
