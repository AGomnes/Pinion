using Microsoft.CodeAnalysis;
using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

public class SourceScanLoaderTests : IDisposable
{
    private readonly string _dir;

    public SourceScanLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pinion-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch {  }
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    [Fact]
    public void Partitions_production_and_test_files_and_skips_bin_obj()
    {
        Write("Calculator.cs", "namespace S { public class Calculator { public int Add(int a,int b)=>a+b; } }");
        Write("CalculatorTests.cs", "using Xunit; namespace S { public class CalculatorTests { } }");

        Directory.CreateDirectory(Path.Combine(_dir, "obj"));
        File.WriteAllText(Path.Combine(_dir, "obj", "Generated.cs"), "namespace S { class G {} }");

        var solution = SourceScanLoader.Load(_dir, log: null);

        var prod = solution.Projects.Single(p => p.Name == "ScannedSources");
        var tests = solution.Projects.Single(p => p.Name == "ScannedTests");

        var prodDocs = prod.Documents.Where(d => d.Name != "PinionGlobalUsings.cs").ToList();
        var testDocs = tests.Documents.Where(d => d.Name != "PinionGlobalUsings.cs").ToList();

        Assert.Single(prodDocs);
        Assert.Equal("Calculator.cs", prodDocs.Single().Name);
        Assert.Single(testDocs);
        Assert.Equal("CalculatorTests.cs", testDocs.Single().Name);
    }

    [Fact]
    public void Production_only_tree_has_resolvable_method_symbols()
    {
        Write("Money.cs", "namespace S { public class Money { public decimal Vat(decimal a)=>a*0.25m; } }");

        var solution = SourceScanLoader.Load(_dir, log: null);
        var project = solution.Projects.Single(p => p.Name == "ScannedSources");

        Assert.Empty(solution.Projects.Where(p => p.Name == "ScannedTests"));
        Assert.Single(project.Documents.Where(d => d.Name != "PinionGlobalUsings.cs"));
        Assert.NotEmpty(project.MetadataReferences);
    }
}
