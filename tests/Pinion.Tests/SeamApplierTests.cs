using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

public class SeamApplierTests
{
    [Fact]
    public async Task Plans_and_applies_on_a_bare_source_directory()
    {
        // No MSBuild in the test host → the applier's source-scan path; no .csproj → the compile gate is
        // skipped with a note (never silently). This is the whole plan→apply flow minus `dotnet build`.
        string dir = Path.Combine(Path.GetTempPath(), "pinion-seam-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Log.cs");
        try
        {
            await File.WriteAllTextAsync(file, """
                using System;
                public class Log
                {
                    public string Stamp(string a) => DateTime.Now.ToString("o") + a;
                }
                """);

            using var applier = new SeamApplier();
            var result = await applier.PlanAsync(dir, target: null, default);

            var plan = Assert.Single(result.Plans);
            Assert.Equal(file, plan.FilePath);
            Assert.Contains("Log.Stamp", plan.Methods);
            Assert.Contains("DateTime.Now", plan.Reads);
            Assert.NotEqual("", plan.Diff);                    // human-reviewable preview
            Assert.Equal(plan.OriginalText, await File.ReadAllTextAsync(file)); // planning never writes

            var (ok, notes) = await applier.ApplyAsync(dir, result.Plans, default);

            Assert.True(ok);
            Assert.Contains(notes, n => n.Contains("compile gate skipped"));   // bare dir → honest note
            string written = await File.ReadAllTextAsync(file);
            Assert.Contains("Stamp(a, DateTime.Now)", written);                // wrapper
            Assert.Contains("global::System.DateTime now", written);           // deterministic overload
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
