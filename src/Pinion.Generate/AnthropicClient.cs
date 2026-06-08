using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pinion.Generate;

/// <summary>
/// A thin, auditable HTTP client for the Anthropic Messages API. Deliberately tiny: one
/// outbound endpoint, no telemetry, no hidden calls. The base URL is overridable so the
/// same client targets a local/air-gapped Anthropic-compatible endpoint unchanged.
/// </summary>
/// <remarks>
/// Zero Data Retention is an organization-level setting on the Anthropic account, not a
/// request header — enable it in the console and verify the current terms; this client
/// does not (and cannot) assert it via the wire.
/// </remarks>
public sealed class AnthropicClient : ILlmClient, IDisposable
{
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly bool _ownsHttp;

    public AnthropicClient(string apiKey, string baseUrl = "https://api.anthropic.com", HttpClient? http = null)
    {
        _apiKey = apiKey;
        _endpoint = baseUrl.TrimEnd('/') + "/v1/messages";
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = http is null;
    }

    public string Name => "anthropic";

    /// <summary>The exact JSON body that will be POSTed — also used by `--dry-run` so the
    /// user sees precisely what bytes would leave the machine.</summary>
    public static string BuildRequestJson(LlmRequest request)
    {
        object systemValue = request.CacheSystem
            ? new object[]
            {
                new { type = "text", text = request.System, cache_control = new { type = "ephemeral" } },
            }
            : request.System;

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = systemValue,
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };

        return JsonSerializer.Serialize(body, Json);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(BuildRequestJson(request), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        httpReq.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        string responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new LlmApiException((int)resp.StatusCode, $"Anthropic API {(int)resp.StatusCode}: {Truncate(responseBody, 500)}");

        return Parse(responseBody);
    }

    internal static LlmResponse Parse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }

        var usage = new LlmUsage();
        if (root.TryGetProperty("usage", out var u))
        {
            usage = new LlmUsage(
                InputTokens: GetInt(u, "input_tokens"),
                OutputTokens: GetInt(u, "output_tokens"),
                CacheReadInputTokens: GetInt(u, "cache_read_input_tokens"),
                CacheCreationInputTokens: GetInt(u, "cache_creation_input_tokens"));
        }

        return new LlmResponse(sb.ToString(), usage);
    }

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
