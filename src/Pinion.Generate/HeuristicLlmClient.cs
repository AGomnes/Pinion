using System.Text;
using System.Text.RegularExpressions;

namespace Pinion.Generate;

/// <summary>
/// An offline, deterministic stand-in for the LLM. It synthesizes a compiling
/// xUnit+Verify characterization test from the structured header in the prompt — no
/// network, no key. Quality is intentionally modest (it doesn't reason about inputs the
/// way a model does); its job is to make the full generate→compile→run→snapshot pipeline
/// runnable and testable offline, and to serve as a no-API baseline / CI provider.
/// </summary>
public sealed class HeuristicLlmClient : ILlmClient
{
    public string Name => "heuristic";

    public string PreviewRequest(LlmRequest request) =>
        "(provider 'heuristic' is offline: no request is built and nothing is sent)";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        string firstUser = request.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
        string ns = HeaderValue(firstUser, "Namespace");
        string type = HeaderValue(firstUser, "Type");
        string signature = HeaderValue(firstUser, "Target method");

        string source = CSharpTestSynthesizer.Synthesize(ns, type, signature);
        return Task.FromResult(new LlmResponse(source, new LlmUsage()));
    }

    private static string HeaderValue(string text, string key)
    {
        var m = Regex.Match(text, $@"^{Regex.Escape(key)}:\s*(?<v>.+)$", RegexOptions.Multiline);
        return m.Success ? m.Groups["v"].Value.Trim() : "";
    }
}

/// <summary>Turns a method signature into a compiling characterization test (best-effort).</summary>
internal static class CSharpTestSynthesizer
{
    public static string Synthesize(string ns, string type, string signature)
    {
        var (methodName, returnsVoid, isStatic, parameters) = ParseSignature(signature);
        string safeMethod = SafeIdentifier(methodName);
        string className = $"{type}_{safeMethod}_CharacterizationTests";

        var sampleSets = parameters.Select(p => SampleValues(p.Type)).ToList();
        int rows = sampleSets.Count == 0 ? 1 : Math.Min(3, sampleSets.Max(s => s.Count));

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using VerifyXunit;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine("using static VerifyXunit.Verifier;");
        if (!string.IsNullOrEmpty(ns)) sb.AppendLine($"using {ns};");
        sb.AppendLine();
        sb.AppendLine("namespace Pinion.Generated;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    [Fact]");
        sb.AppendLine($"    public async Task {safeMethod}_characterization()");
        sb.AppendLine("    {");
        sb.AppendLine("        var entries = new List<object>();");
        if (!isStatic) sb.AppendLine($"        var sut = new {type}();");
        sb.AppendLine();

        for (int r = 0; r < rows; r++)
        {
            var args = parameters.Select((p, i) => sampleSets[i][Math.Min(r, sampleSets[i].Count - 1)]).ToList();
            string argList = string.Join(", ", args);
            string inputDesc = parameters.Count == 0 ? "()" : "(" + string.Join(", ", args).Replace("\"", "'") + ")";
            string target = isStatic ? type : "sut";

            sb.AppendLine("        try");
            sb.AppendLine("        {");
            if (returnsVoid)
            {
                sb.AppendLine($"            {target}.{methodName}({argList});");
                sb.AppendLine($"            entries.Add(new {{ Input = \"{Escape(inputDesc)}\", Outcome = (object?)\"void (no return value)\" }});");
            }
            else
            {
                sb.AppendLine($"            var result = {target}.{methodName}({argList});");
                sb.AppendLine($"            entries.Add(new {{ Input = \"{Escape(inputDesc)}\", Outcome = (object?)result }});");
            }
            sb.AppendLine("        }");
            sb.AppendLine("        catch (Exception ex)");
            sb.AppendLine("        {");
            sb.AppendLine($"            entries.Add(new {{ Input = \"{Escape(inputDesc)}\", Outcome = (object?)(ex.GetType().Name + \": \" + ex.Message) }});");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        await Verify(entries);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (string Name, bool ReturnsVoid, bool IsStatic, List<(string Type, string Name)> Parameters)
        ParseSignature(string signature)
    {
        bool isStatic = Regex.IsMatch(signature, @"\bstatic\b");
        int paren = signature.IndexOf('(');
        string head = paren >= 0 ? signature[..paren] : signature;
        string between = paren >= 0 ? signature[(paren + 1)..signature.LastIndexOf(')')] : "";

        var headTokens = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string name = headTokens.Length > 0 ? headTokens[^1] : "Unknown";
        bool returnsVoid = headTokens.Any(t => t == "void");

        var parameters = new List<(string, string)>();
        foreach (var raw in SplitTopLevel(between))
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            p = Regex.Replace(p, @"^(out|ref|in|params)\s+", "");
            int eq = p.IndexOf('=');
            if (eq >= 0) p = p[..eq].Trim();
            int lastSpace = p.LastIndexOf(' ');
            if (lastSpace < 0) continue;
            parameters.Add((p[..lastSpace].Trim(), p[(lastSpace + 1)..].Trim()));
        }

        return (name, returnsVoid, isStatic, parameters);
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        if (start < s.Length) yield return s[start..];
    }

    private static List<string> SampleValues(string type)
    {
        string t = type.TrimEnd('?').Trim();
        return t switch
        {
            "int" or "Int32" or "long" or "Int64" or "short" or "byte" => new() { "0", "1", "-1" },
            "decimal" or "Decimal" => new() { "0m", "100.5m", "-1m" },
            "double" or "Double" => new() { "0.0", "1.5", "-1.0" },
            "float" or "Single" => new() { "0f", "1.5f" },
            "bool" or "Boolean" => new() { "true", "false" },
            "string" or "String" => new() { "\"\"", "\"abc\"", "null" },
            "char" or "Char" => new() { "'a'", "'0'" },
            _ => new() { $"default({type})!" },
        };
    }

    private static string SafeIdentifier(string s) => s == "ctor" ? "Constructor" : Regex.Replace(s, @"\W", "_");
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
