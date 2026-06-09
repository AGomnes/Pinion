using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// The runner spawns `dotnet test`, which executes the (untrusted, legacy) code under characterization.
/// That child must NOT inherit Pinion's API-key secrets, or a target method could read them out of its
/// own environment and exfiltrate them.
/// </summary>
public class ProcessRunnerTests
{
    [Fact]
    public void ScrubSecrets_removes_known_secret_env_vars_but_keeps_the_rest()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = "sk-ant-secret",
            ["OPENAI_API_KEY"] = "sk-openai-secret",
            ["PATH"] = "/usr/bin",        // the runner legitimately needs these
            ["DOTNET_ROOT"] = "/dotnet",
            ["NUGET_PACKAGES"] = "/nuget",
        };

        ProcessRunner.ScrubSecretsFrom(env);

        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY")); // Pinion's required secret is gone
        Assert.False(env.ContainsKey("OPENAI_API_KEY"));
        Assert.True(env.ContainsKey("PATH"));               // benign vars are preserved
        Assert.True(env.ContainsKey("DOTNET_ROOT"));
        Assert.True(env.ContainsKey("NUGET_PACKAGES"));
    }

    [Fact]
    public async Task Spawned_child_cannot_read_ANTHROPIC_API_KEY_from_its_environment()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-MUSTNOTLEAK");
        try
        {
            // Echo the variable the way the child's shell would expand it. With the var scrubbed, the
            // value never appears; if it leaked, the output would contain it.
            (string file, string[] args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", "echo key=[%ANTHROPIC_API_KEY%]" })
                : ("/bin/sh", new[] { "-c", "echo key=[$ANTHROPIC_API_KEY]" });

            var result = await ProcessRunner.RunAsync(file, args);

            Assert.Contains("key=[", result.StdOut);              // sanity: the child actually ran
            Assert.DoesNotContain("MUSTNOTLEAK", result.StdOut);  // ...but never saw the key
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }
}
