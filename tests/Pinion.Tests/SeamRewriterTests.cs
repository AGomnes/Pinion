using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// The `pinion seam` transform, asserted at two levels: the rewritten TEXT has the wrapper+overload
/// shape, and the rewritten source actually COMPILES (in-memory, real references) — because a
/// refactoring tool that emits non-compiling code is worse than none.
/// </summary>
public class SeamRewriterTests
{
    private static readonly IReadOnlyList<MetadataReference> Refs =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

    private static string Rewrite(string src, out IReadOnlyList<(string Method, string Reason)> skipped)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var comp = CSharpCompilation.Create("T", new[] { tree }, Refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);
        var (newRoot, _, skips) = SeamRewriter.RewriteDocument(tree.GetRoot(), model, include: null, default);
        skipped = skips;
        return newRoot.ToFullString();
    }

    private static void AssertCompiles(string src)
    {
        var comp = CSharpCompilation.Create("T2", new[] { CSharpSyntaxTree.ParseText(src) }, Refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "rewritten source does not compile:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Instance_method_reading_DateTime_Now_gets_wrapper_plus_deterministic_overload()
    {
        string rewritten = Rewrite("""
            using System;
            public class AuditLog
            {
                public string Stamp(string action) => DateTime.Now.ToString("o") + " " + action;
            }
            """, out var skipped);

        Assert.Empty(skipped);
        Assert.Contains("Stamp(action, DateTime.Now)", rewritten);            // wrapper delegates with the original expression
        Assert.Contains("global::System.DateTime now", rewritten);            // overload takes the value
        Assert.Contains("now.ToString(\"o\")", rewritten);                    // body reads the parameter, not the ambient
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Static_method_and_Guid_NewGuid_are_seamed()
    {
        string rewritten = Rewrite("""
            using System;
            public static class Ids
            {
                public static string Next(string prefix)
                {
                    return prefix + Guid.NewGuid().ToString("N");
                }
            }
            """, out var skipped);

        Assert.Empty(skipped);
        Assert.Contains("Next(prefix, Guid.NewGuid())", rewritten);
        Assert.Contains("global::System.Guid newGuid", rewritten);
        Assert.Contains("newGuid.ToString(\"N\")", rewritten);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Multiple_ambients_become_multiple_parameters_in_first_occurrence_order()
    {
        string rewritten = Rewrite("""
            using System;
            public class Order
            {
                public string Receipt()
                {
                    var id = Guid.NewGuid();
                    var at = DateTime.UtcNow;
                    return id + "@" + at.Ticks;
                }
            }
            """, out _);

        Assert.Contains("Receipt(Guid.NewGuid(), DateTime.UtcNow)", rewritten); // wrapper, occurrence order
        Assert.Contains("global::System.Guid newGuid, global::System.DateTime utcNow", rewritten);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Repeated_reads_of_the_same_ambient_share_one_parameter()
    {
        string rewritten = Rewrite("""
            using System;
            public class C
            {
                public double Age(DateTime born) => (DateTime.Now - born).TotalDays + DateTime.Now.Millisecond;
            }
            """, out _);

        // One `now` parameter, both reads replaced (also freezes the double-read — deterministic by design).
        Assert.Contains("(now - born).TotalDays + now.Millisecond", rewritten);
        Assert.Single(rewritten.Split("global::System.DateTime now").Skip(1)); // parameter declared exactly once
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Async_method_keeps_async_body_but_wrapper_is_not_async()
    {
        string rewritten = Rewrite("""
            using System;
            using System.Threading.Tasks;
            public class C
            {
                public async Task<long> TicksAsync()
                {
                    await Task.Yield();
                    return DateTime.Now.Ticks;
                }
            }
            """, out _);

        Assert.Contains("public Task<long> TicksAsync() => TicksAsync(DateTime.Now);", rewritten.Replace("  ", " "));
        Assert.Contains("public async Task<long> TicksAsync(global::System.DateTime now)", rewritten);
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Param_name_collision_falls_back_to_a_suffixed_name()
    {
        string rewritten = Rewrite("""
            using System;
            public class C
            {
                public double M(DateTime now) => (DateTime.Now - now).TotalDays;
            }
            """, out var skipped);

        Assert.Empty(skipped);
        Assert.Contains("nowSeam", rewritten); // `now` is taken by the user's parameter
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Rewrite_is_idempotent_a_second_run_finds_only_the_wrapper_and_skips_it()
    {
        string once = Rewrite("""
            using System;
            public class C
            {
                public long Ticks() => DateTime.Now.Ticks;
            }
            """, out _);

        string twice = Rewrite(once, out var skipped);

        Assert.Equal(once, twice); // nothing further to do
        Assert.Contains(skipped, s => s.Reason.Contains("already seamed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generic_and_override_edges_are_handled_safely()
    {
        // Generic method → skipped with a manual reason; override → wrapper keeps `override`, overload drops it.
        string rewritten = Rewrite("""
            using System;
            public abstract class Base { public abstract string Who(); }
            public class C : Base
            {
                public T Tag<T>(T x) { _ = DateTime.Now; return x; }
                public override string Who() => "at " + DateTime.Now.Ticks;
            }
            """, out var skipped);

        Assert.Contains(skipped, s => s.Method == "C.Tag" && s.Reason.Contains("generic"));
        Assert.Contains("public override string Who() => Who(DateTime.Now);", rewritten.Replace("  ", " "));
        Assert.Contains("public string Who(global::System.DateTime now)", rewritten); // no `override` on the overload
        AssertCompiles(rewritten);
    }

    [Fact]
    public void Nameof_is_never_rewritten()
    {
        string src = """
            using System;
            public class C
            {
                public string M() => nameof(DateTime.Now);
            }
            """;
        string rewritten = Rewrite(src, out _);

        Assert.Equal(src, rewritten); // a nameof read is not a behavioral dependency — untouched
    }

    [Fact]
    public void Seamable_kinds_stay_in_lockstep_with_the_seam_analyzer_vocabulary()
    {
        // Every ambient `pinion seam` fixes must be something `analyze` reports as an obstacle —
        // otherwise the tool would fix things the report never told the user about (or vice versa).
        foreach (var spec in SeamRewriter.Specs.Values)
        {
            string member = spec.Display.EndsWith("NewGuid") ? spec.Display + "()" : spec.Display;
            string src = $$"""
                using System;
                public class C
                {
                    public string M() => {{member}}.ToString();
                }
                """;
            var tree = CSharpSyntaxTree.ParseText(src);
            var comp = CSharpCompilation.Create("T", new[] { tree }, Refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var model = comp.GetSemanticModel(tree);
            var method = tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single();
            var symbol = (IMethodSymbol)model.GetDeclaredSymbol(method)!;

            var (_, obstacles) = SeamAnalyzer.Analyze(symbol, method.ExpressionBody, model, default);

            Assert.Contains(spec.Display, obstacles);
        }
    }
}
