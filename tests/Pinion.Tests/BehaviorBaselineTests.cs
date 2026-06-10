using Pinion.Engine.Reporting;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class BehaviorBaselineTests
{
    [Fact]
    public void Accept_promotes_received_to_verified_and_removes_received()
    {
        using var tmp = new TempDir();
        string verified = Path.Combine(tmp.Path, "X.verified.txt");
        string received = Path.Combine(tmp.Path, "X.received.txt");
        File.WriteAllText(verified, "old behavior");
        File.WriteAllText(received, "new behavior");
        var entry = new BehaviorDiffEntry("X.M", BehaviorChange.Changed, "diff", verified, received);

        int accepted = BehaviorBaseline.Accept(new[] { entry });

        Assert.Equal(1, accepted);
        Assert.Equal("new behavior", File.ReadAllText(verified)); // current output is now the golden master
        Assert.False(File.Exists(received));                      // received consumed
    }

    [Fact]
    public void Accept_skips_entries_with_no_received_file()
    {
        var entry = new BehaviorDiffEntry("X.M", BehaviorChange.Identical, null, "does-not-matter.verified.txt", null);

        Assert.Equal(0, BehaviorBaseline.Accept(new[] { entry }));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pinion-accept-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
