using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// Drives the REAL <see cref="OpenAiClient"/> over real HTTP against a scripted local server, so the
/// opt-in OpenAI path is CI-covered for $0: correct route, correct auth header per flavor, and error
/// mapping. Mirrors <see cref="AnthropicClientHttpTests"/>.
/// </summary>
public class OpenAiClientHttpTests
{
    [Fact]
    public async Task Posts_to_chat_completions_with_a_bearer_token()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueRaw(200, """
        { "choices": [ { "message": { "content": "OK" } } ],
          "usage": { "prompt_tokens": 11, "completion_tokens": 3 } }
        """);

        using var client = new OpenAiClient("secret-key", server.BaseUrl);
        var resp = await client.CompleteAsync(
            new LlmRequest("gpt-4.1", "SYS", new[] { LlmMessage.User("HI") }, 256), default);

        Assert.Equal("OK", resp.Text);
        Assert.Equal(11, resp.Usage.InputTokens);

        var recorded = Assert.Single(server.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v1/chat/completions", recorded.Path);
        Assert.Equal("Bearer secret-key", recorded.Headers["Authorization"]);
        Assert.Contains("\"role\":\"system\"", recorded.Body.Replace(" ", ""));
    }

    [Fact]
    public async Task Azure_flavor_uses_the_api_key_header_and_deployment_route()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueRaw(200, """{ "choices": [ { "message": { "content": "A" } } ] }""");

        using var client = new OpenAiClient("azure-secret", server.BaseUrl,
            OpenAiClient.AuthStyle.AzureApiKey, azureDeployment: "prod-deploy");
        await client.CompleteAsync(new LlmRequest("prod-deploy", "S", new[] { LlmMessage.User("H") }, 64), default);

        var recorded = Assert.Single(server.Requests);
        Assert.Equal("azure-secret", recorded.Headers["api-key"]);
        Assert.False(recorded.Headers.ContainsKey("Authorization"));
        Assert.Contains("/openai/deployments/prod-deploy/chat/completions", recorded.Path);
    }

    [Fact]
    public async Task Api_error_becomes_LlmApiException_with_the_status()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueRaw(401, """{ "error": { "message": "bad key" } }""");

        using var client = new OpenAiClient("k", server.BaseUrl) { RetryBaseDelay = TimeSpan.Zero };
        var ex = await Assert.ThrowsAsync<LlmApiException>(() => client.CompleteAsync(
            new LlmRequest("gpt-4.1", "S", new[] { LlmMessage.User("H") }, 64), default));

        Assert.Equal(401, ex.StatusCode);
        Assert.True(ex.ShouldAbortRun);
    }

    [Fact]
    public async Task Transient_failure_is_retried_then_succeeds()
    {
        using var server = new MockAnthropicServer();
        server.EnqueueRaw(503, """{ "error": { "message": "unavailable" } }""");
        server.EnqueueRaw(200, """{ "choices": [ { "message": { "content": "RECOVERED" } } ] }""");

        using var client = new OpenAiClient("k", server.BaseUrl) { MaxRetries = 2, RetryBaseDelay = TimeSpan.Zero };
        var resp = await client.CompleteAsync(
            new LlmRequest("gpt-4.1", "S", new[] { LlmMessage.User("H") }, 64), default);

        Assert.Equal("RECOVERED", resp.Text);
        Assert.Equal(2, server.Requests.Count);
    }
}
