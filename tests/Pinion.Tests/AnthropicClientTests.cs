using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class AnthropicClientTests
{
    [Fact]
    public void Request_json_caches_the_system_prefix_and_carries_model_and_messages()
    {
        var req = new LlmRequest("claude-sonnet-4-6", "SYS", new[] { LlmMessage.User("hello") }, 4096);
        string json = AnthropicClient.BuildRequestJson(req);

        Assert.Contains("\"model\":\"claude-sonnet-4-6\"", json);
        Assert.Contains("\"max_tokens\":4096", json);
        Assert.Contains("\"cache_control\":{\"type\":\"ephemeral\"}", json); // stable prefix is cached
        Assert.Contains("\"role\":\"user\"", json);
        Assert.Contains("hello", json);
    }

    [Fact]
    public void Request_json_never_carries_sampling_or_thinking_fields()
    {
        // These 400 on Opus 4.8/4.7 and aren't needed for characterization. The request is built from
        // a fixed field set, so they can never appear — pin that so a future edit can't regress it.
        var req = new LlmRequest("claude-opus-4-8", "SYS", new[] { LlmMessage.User("hi") }, 4096);
        string json = AnthropicClient.BuildRequestJson(req);

        foreach (var forbidden in new[] { "temperature", "top_p", "top_k", "thinking", "budget_tokens" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public void Caching_can_be_turned_off_for_the_system_prefix()
    {
        var req = new LlmRequest("claude-sonnet-4-6", "SYS", new[] { LlmMessage.User("hi") }, 4096, CacheSystem: false);
        string json = AnthropicClient.BuildRequestJson(req);

        Assert.DoesNotContain("cache_control", json);
        Assert.Contains("\"system\":\"SYS\"", json); // plain string, not the cached-block form
    }

    [Fact]
    public void Parse_concatenates_text_blocks_and_reads_usage()
    {
        // A representative Messages API response body.
        const string body = """
        {
          "id": "msg_1",
          "type": "message",
          "role": "assistant",
          "content": [
            {"type": "text", "text": "using System;"},
            {"type": "text", "text": " class X {}"}
          ],
          "usage": {"input_tokens": 12, "output_tokens": 34, "cache_read_input_tokens": 8}
        }
        """;

        var resp = AnthropicClient.Parse(body);

        Assert.Equal("using System; class X {}", resp.Text);
        Assert.Equal(12, resp.Usage.InputTokens);
        Assert.Equal(34, resp.Usage.OutputTokens);
        Assert.Equal(8, resp.Usage.CacheReadInputTokens);
    }
}

public class ModelCatalogTests
{
    [Theory]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-haiku-4-5", true)]
    [InlineData("claude-sonnet-4.6", false)]  // common typo: dots instead of dashes
    [InlineData("gpt-4", false)]
    [InlineData("", false)]
    public void Known_ids_are_recognized_typos_are_not(string model, bool known)
    {
        Assert.Equal(known, ModelCatalog.IsKnown(model));
    }
}

public class ExtractCodeTests
{
    [Fact]
    public void Prefers_fenced_block()
    {
        Assert.Equal("class X {}", TestGenerator.ExtractCode("Sure:\n```csharp\nclass X {}\n```\nEnjoy."));
    }

    [Fact]
    public void Strips_leading_prose_when_unfenced()
    {
        string reply = "Here is the characterization test:\n\nusing Xunit;\nclass T {}";
        Assert.Equal("using Xunit;\nclass T {}", TestGenerator.ExtractCode(reply));
    }

    [Fact]
    public void Returns_pure_code_unchanged()
    {
        Assert.Equal("namespace N;", TestGenerator.ExtractCode("  namespace N;  "));
    }
}
