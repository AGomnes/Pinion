using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class SeamAnalyzerTests
{
    /// <summary>Compile a snippet in-memory and run the seam analysis on one of its methods.</summary>
    private static (IReadOnlyList<string> Seams, IReadOnlyList<string> Obstacles) Analyze(string source, string method)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create("SeamTest", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = comp.GetSemanticModel(tree);
        var decl = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(d => d.Identifier.Text == method);
        var symbol = (IMethodSymbol)model.GetDeclaredSymbol(decl)!;
        SyntaxNode? body = (SyntaxNode?)decl.Body ?? decl.ExpressionBody;
        return SeamAnalyzer.Analyze(symbol, body, model, default);
    }

    [Fact]
    public void Constructor_injected_abstraction_is_a_seam()
    {
        const string src = """
            public interface IClock { System.DateTime Now { get; } }
            public class OrderService
            {
                private readonly IClock _clock;
                public OrderService(IClock clock) { _clock = clock; }
                public int Year() => _clock.Now.Year;
            }
            """;
        var (seams, obstacles) = Analyze(src, "Year");

        Assert.Contains("IClock", seams);
        Assert.Empty(obstacles);
    }

    [Fact]
    public void Parameter_abstraction_is_a_seam()
    {
        const string src = """
            public interface IRule { int Apply(int x); }
            public class Engine { public int Run(IRule rule, int x) => rule.Apply(x); }
            """;
        var (seams, _) = Analyze(src, "Run");
        Assert.Contains("IRule", seams);
    }

    [Fact]
    public void Ambient_and_io_access_are_obstacles_not_seams()
    {
        const string src = """
            public class Reporter
            {
                public void Write()
                {
                    var now = System.DateTime.Now;
                    System.IO.File.WriteAllText("out.txt", now.ToString());
                }
            }
            """;
        var (seams, obstacles) = Analyze(src, "Write");

        Assert.Contains("DateTime.Now", obstacles);
        Assert.Contains("File", obstacles);
        Assert.Empty(seams);
    }

    [Fact]
    public void Collection_interfaces_are_not_treated_as_seams()
    {
        const string src = """
            using System.Collections.Generic;
            public class Agg
            {
                public int Sum(IEnumerable<int> xs) { int s = 0; foreach (var x in xs) s += x; return s; }
            }
            """;
        var (seams, obstacles) = Analyze(src, "Sum");

        Assert.DoesNotContain("IEnumerable<int>", seams);
        Assert.Empty(obstacles);
    }

    [Fact]
    public void Pure_method_has_no_seams_and_no_obstacles()
    {
        const string src = "public class Calc { public int Add(int a, int b) => a + b; }";
        var (seams, obstacles) = Analyze(src, "Add");
        Assert.Empty(seams);
        Assert.Empty(obstacles);
    }

    [Fact]
    public void CodeUnit_seamability_is_derived_from_the_two_lists()
    {
        Assert.Equal(Seamability.Pure, Unit().Seamability);
        Assert.Equal(Seamability.SeamAvailable, Unit(seams: new[] { "IClock" }).Seamability);
        Assert.Equal(Seamability.NeedsSeam, Unit(obstacles: new[] { "DateTime.Now" }).Seamability);
        Assert.Equal(Seamability.SeamAvailable, Unit(seams: new[] { "IClock" }, obstacles: new[] { "File" }).Seamability);
    }

    [Fact]
    public void Report_counts_high_risk_units_that_need_a_seam()
    {
        var needsSeam = Unit(obstacles: new[] { "DateTime.Now" }, complexity: 25, displayName: "A.Risky");
        var hasSeam = Unit(seams: new[] { "IClock" }, complexity: 25, displayName: "B.Wrappable");

        var report = ReportBuilder.Build("proj", new[] { needsSeam, hasSeam });

        Assert.Equal(1, report.SeamsToIntroduce);
    }

    private static CodeUnit Unit(
        string[]? seams = null, string[]? obstacles = null, int complexity = 1, string displayName = "T.M") =>
        new("S." + displayName, displayName, "F.cs", 1, 1, "sig", Array.Empty<ParamInfo>(), "void",
            complexity, 1, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            HasTests: false, IsPublicEntryPoint: true, MigrationLandmines: Array.Empty<string>(),
            Seams: seams, SeamObstacles: obstacles);
}
