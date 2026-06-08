using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class SecretScrubberTests
{
    [Fact]
    public void Redacts_password_assignment()
    {
        var r = SecretScrubber.Scrub("""var password = "hunter2";""");
        Assert.DoesNotContain("hunter2", r.Text);
        Assert.Contains("[REDACTED]", r.Text);
        Assert.Equal(1, r.Redactions);
    }

    [Fact]
    public void Redacts_connection_string_password()
    {
        var r = SecretScrubber.Scrub("Server=db;Database=app;User Id=sa;Password=S3cr3t;");
        Assert.DoesNotContain("S3cr3t", r.Text);
    }

    [Fact]
    public void Redacts_known_token_shapes()
    {
        var r = SecretScrubber.Scrub("const key = sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123;");
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", r.Text);
        Assert.Contains("[REDACTED]", r.Text);
    }

    [Fact]
    public void Leaves_ordinary_code_untouched()
    {
        const string code = "public decimal CalculateVat(decimal amount) => amount * 0.25m;";
        var r = SecretScrubber.Scrub(code);
        Assert.Equal(code, r.Text);
        Assert.Equal(0, r.Redactions);
    }
}

public class PromptAndExtractionTests
{
    [Fact]
    public void System_prompt_states_the_record_not_assert_rule()
    {
        string sys = PromptBuilder.System();
        Assert.Contains("RECORD", sys);
        Assert.Contains("DO NOT assert", sys);
    }

    [Fact]
    public void ExtractCode_strips_markdown_fences()
    {
        string fenced = "Here you go:\n```csharp\nclass X {}\n```\nDone.";
        Assert.Equal("class X {}", TestGenerator.ExtractCode(fenced));
    }

    [Fact]
    public void ExtractCode_returns_plain_text_unchanged()
    {
        Assert.Equal("class X {}", TestGenerator.ExtractCode("  class X {}  "));
    }
}

public class HeuristicLlmClientTests
{
    private static CodeUnit Unit() => new(
        "LegacyShop.InvoiceService.CalculateVat(decimal, string, bool)",
        "InvoiceService.CalculateVat", "Invoice.cs", 1, 5,
        "public decimal CalculateVat(decimal amount, string region, bool isExempt)",
        Array.Empty<ParamInfo>(), "decimal", 5, 5,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        false, true, Array.Empty<string>());

    [Fact]
    public async Task Synthesizes_a_compilable_looking_verify_test()
    {
        var ctx = new GenerationContext(Unit(), "LegacyShop", "InvoiceService",
            "public class InvoiceService { }", Array.Empty<string>());

        var request = new LlmRequest("m", PromptBuilder.System(),
            new[] { LlmMessage.User(PromptBuilder.InitialUser(ctx)) }, 4096);

        var resp = await new HeuristicLlmClient().CompleteAsync(request, default);

        Assert.Contains("class InvoiceService_CalculateVat_CharacterizationTests", resp.Text);
        Assert.Contains("[Fact]", resp.Text);
        Assert.Contains("await Verify(entries)", resp.Text);
        Assert.Contains("new InvoiceService()", resp.Text);
        // Never asserts expected values — the snapshot is the assertion.
        Assert.DoesNotContain("Assert.Equal", resp.Text);
    }
}
