using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class GeneratedSetupTests
{
    [Fact]
    public async Task Emitting_a_test_writes_a_culture_pinning_setup_file()
    {
        using var tmp = new TempDir();
        string proj = Path.Combine(tmp.Path, "T.csproj");
        File.WriteAllText(proj, "<Project/>");

        using var gen = new CSharpTestGenerator();
        gen.ConfigureGeneration(proj);
        var unit = new CodeUnit("S.M()", "S.M", "S.cs", 1, 2, "sig",
            Array.Empty<ParamInfo>(), "void", 1, 1, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), HasTests: false, IsPublicEntryPoint: true, Array.Empty<string>());

        await gen.EmitTestAsync(unit, "public class S_M_CharacterizationTests {}", default);

        string setup = Path.Combine(tmp.Path, "PinionCharacterization", "__PinionGeneratedSetup.cs");
        Assert.True(File.Exists(setup), "culture-pinning setup file was not written");
        string txt = await File.ReadAllTextAsync(setup);
        Assert.Contains("[ModuleInitializer]", txt);
        Assert.Contains("CultureInfo.InvariantCulture", txt);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pinion-setup-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
