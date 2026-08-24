using System.Text.Json;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// The OpenAI-compatible provider: wire format, response/usage parsing, and the property that matters
/// most for trust — each provider previews its OWN payload, so `--dry-run` never shows bytes the tool
/// would not actually send.
/// </summary>
public class OpenAiClientTests
{
    private static LlmRequest Req(int maxTokens = 1024) => new(
        Model: "gpt-4.1",
        System: "SYSTEM-PREFIX",
        Messages: new[] { LlmMessage.User("USER-BODY") },
        MaxTokens: maxTokens);

    [Fact]
    public void Request_uses_chat_completions_shape_with_system_as_first_message()
    {
        using var client = new OpenAiClient("k");
        using var doc = JsonDocument.Parse(client.BuildRequestJson(Req()));
        var root = doc.RootElement;

        Assert.Equal("gpt-4.1", root.GetProperty("model").GetString());

        // Chat Completions has no separate system field: the cacheable prefix leads the messages array.
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM-PREFIX", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER-BODY", messages[1].GetProperty("content").GetString());

        // Anthropic-only fields must not leak into an OpenAI body.
        Assert.False(root.TryGetProperty("system", out _));
        Assert.DoesNotContain("cache_control", client.BuildRequestJson(Req()));
    }

    [Theory]
    [InlineData("https://api.openai.com", "max_completion_tokens")]
    [InlineData("https://my-res.openai.azure.com", "max_completion_tokens")]
    [InlineData("http://localhost:11434", "max_tokens")]
    public void Token_limit_field_matches_the_endpoint(string baseUrl, string expectedField)
    {
        // Official endpoints moved to max_completion_tokens; OpenAI-compatible servers (Ollama, vLLM,
        // LM Studio, older gateways) still expect max_tokens. Sending the wrong one is a 400.
        using var client = new OpenAiClient("k", baseUrl);
        using var doc = JsonDocument.Parse(client.BuildRequestJson(Req(777)));
        Assert.Equal(777, doc.RootElement.GetProperty(expectedField).GetInt32());
    }

    [Fact]
    public void Endpoint_is_composed_per_flavor()
    {
        using var openai = new OpenAiClient("k");
        Assert.Equal("https://api.openai.com/v1/chat/completions", openai.Endpoint);

        using var local = new OpenAiClient("k", "http://localhost:11434/");
        Assert.Equal("http://localhost:11434/v1/chat/completions", local.Endpoint);

        // Azure puts the deployment in the path and the version in the query.
        using var azure = new OpenAiClient("k", "https://my-res.openai.azure.com",
            OpenAiClient.AuthStyle.AzureApiKey, azureDeployment: "my-deploy");
        Assert.Contains("/openai/deployments/my-deploy/chat/completions", azure.Endpoint);
        Assert.Contains("api-version=", azure.Endpoint);

        // A gateway that already hands out a full route is left alone.
        using var gateway = new OpenAiClient("k", "https://gw.internal/v1/chat/completions");
        Assert.Equal("https://gw.internal/v1/chat/completions", gateway.Endpoint);
    }

    [Fact]
    public void Azure_without_a_deployment_fails_loudly()
    {
        Assert.Throws<ArgumentException>(() =>
            new OpenAiClient("k", "https://my-res.openai.azure.com", OpenAiClient.AuthStyle.AzureApiKey));
    }

    [Fact]
    public void Response_and_usage_parse_including_cached_prompt_tokens()
    {
        string body = """
        {
          "choices": [ { "message": { "role": "assistant", "content": "GENERATED" } } ],
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 30,
            "prompt_tokens_details": { "cached_tokens": 40 }
          }
        }
        """;

        var parsed = OpenAiClient.Parse(body);

        Assert.Equal("GENERATED", parsed.Text);
        Assert.Equal(30, parsed.Usage.OutputTokens);
        // Cached reads are reported separately and subtracted, so InputTokens keeps meaning
        // "billed as fresh input" — the same convention the Anthropic client uses.
        Assert.Equal(40, parsed.Usage.CacheReadInputTokens);
        Assert.Equal(60, parsed.Usage.InputTokens);
    }

    [Fact]
    public void Usage_absent_or_uncached_still_parses()
    {
        var parsed = OpenAiClient.Parse("""
        { "choices": [ { "message": { "content": "X" } } ], "usage": { "prompt_tokens": 7, "completion_tokens": 2 } }
        """);
        Assert.Equal("X", parsed.Text);
        Assert.Equal(7, parsed.Usage.InputTokens);
        Assert.Equal(0, parsed.Usage.CacheReadInputTokens);
    }

    [Fact]
    public void Each_provider_previews_its_own_wire_format()
    {
        // The point of --dry-run is "these are the exact bytes that would leave". If a provider
        // rendered another provider's shape the preview would be a lie, so this is pinned.
        var req = Req();
        using var anthropic = new AnthropicClient("k");
        using var openai = new OpenAiClient("k");

        string a = anthropic.PreviewRequest(req);
        string o = openai.PreviewRequest(req);

        using var aDoc = JsonDocument.Parse(a);
        using var oDoc = JsonDocument.Parse(o);

        // Anthropic carries the prefix in a top-level `system` field; OpenAI has no such property and
        // folds it into messages[0] instead. (Substring checks would be misleading here: the OpenAI
        // body legitimately contains the text "system" as a role value.)
        Assert.True(aDoc.RootElement.TryGetProperty("system", out _));
        Assert.False(oDoc.RootElement.TryGetProperty("system", out _));
        Assert.Equal("system", oDoc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.NotEqual(a, o);

        // Offline providers say plainly that nothing is sent.
        Assert.Contains("nothing is sent", new HeuristicLlmClient().PreviewRequest(req));
    }

    [Fact]
    public void Provider_name_reflects_the_flavor()
    {
        using var openai = new OpenAiClient("k");
        using var azure = new OpenAiClient("k", "https://r.openai.azure.com",
            OpenAiClient.AuthStyle.AzureApiKey, azureDeployment: "d");
        Assert.Equal("openai", openai.Name);
        Assert.Equal("azure-openai", azure.Name);
    }
}
