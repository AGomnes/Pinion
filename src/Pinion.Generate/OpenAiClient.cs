using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pinion.Generate;

/// <summary>
/// A thin, auditable HTTP client for the OpenAI Chat Completions API and the many servers that speak
/// the same shape: Azure OpenAI, Ollama, vLLM, LM Studio, LiteLLM, and other gateways. Same discipline
/// as <see cref="AnthropicClient"/> — one outbound endpoint, no telemetry, no hidden calls — and, like
/// every provider here, it is reached only when the user explicitly opts in with <c>--provider</c>.
/// </summary>
/// <remarks>
/// Data retention and training terms are account/deployment settings on the provider side, not request
/// headers. Azure OpenAI in your own tenant is the usual choice when those terms must be contractual;
/// this client cannot assert them over the wire, so verify them in your provider's console.
/// </remarks>
public sealed class OpenAiClient : ILlmClient, IDisposable
{
    /// <summary>How the API key is presented. OpenAI and compatible servers use a bearer token;
    /// Azure OpenAI uses its own <c>api-key</c> header.</summary>
    public enum AuthStyle
    {
        Bearer,
        AzureApiKey,
    }

    public const string DefaultBaseUrl = "https://api.openai.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly int[] RetryableStatuses = { 429, 500, 502, 503, 529 };

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly AuthStyle _auth;
    private readonly bool _officialEndpoint;
    private readonly bool _ownsHttp;

    public OpenAiClient(
        string apiKey,
        string baseUrl = DefaultBaseUrl,
        AuthStyle auth = AuthStyle.Bearer,
        string? azureDeployment = null,
        string azureApiVersion = "2024-10-21",
        HttpClient? http = null)
    {
        _apiKey = apiKey;
        _auth = auth;
        _officialEndpoint = IsOfficialEndpoint(baseUrl);
        _endpoint = BuildEndpoint(baseUrl, auth, azureDeployment, azureApiVersion);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = http is null;
    }

    /// <summary>How many times a transient (429/5xx) failure is retried before giving up. (init for tests.)</summary>
    internal int MaxRetries { get; init; } = 4;

    /// <summary>Base of the exponential backoff (delay = Base·2^attempt, capped at 30s). Tests set 0 to avoid waits.</summary>
    internal TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public string Name => _auth == AuthStyle.AzureApiKey ? "azure-openai" : "openai";

    /// <summary>The URL this client will POST to. Surfaced so `--dry-run` and the docs can show it.</summary>
    public string Endpoint => _endpoint;

    /// <summary>
    /// Azure puts the deployment in the path and the version in the query; everyone else uses the
    /// plain Chat Completions route. A caller that already passes a full <c>/chat/completions</c> URL
    /// (some gateways hand one out) is left alone.
    /// </summary>
    private static string BuildEndpoint(string baseUrl, AuthStyle auth, string? azureDeployment, string apiVersion)
    {
        string trimmed = baseUrl.TrimEnd('/');
        if (trimmed.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (auth == AuthStyle.AzureApiKey)
        {
            if (string.IsNullOrWhiteSpace(azureDeployment))
                throw new ArgumentException("Azure OpenAI needs a deployment name (pass --model <deployment>).", nameof(azureDeployment));
            return $"{trimmed}/openai/deployments/{azureDeployment}/chat/completions?api-version={apiVersion}";
        }

        return trimmed + "/v1/chat/completions";
    }

    private static bool IsOfficialEndpoint(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)
        && (u.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The exact JSON body that will be POSTed. Chat Completions has no separate system field, so the
    /// cacheable project prefix becomes the leading <c>system</c> message. Prompt caching is automatic
    /// and server-side here, so there is no per-block cache marker to emit.
    /// </summary>
    public string BuildRequestJson(LlmRequest request)
    {
        var messages = new List<object>(request.Messages.Count + 1);
        if (!string.IsNullOrEmpty(request.System))
            messages.Add(new { role = "system", content = request.System });
        foreach (var m in request.Messages)
            messages.Add(new { role = m.Role, content = m.Content });

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
        };

        // api.openai.com and Azure have moved to max_completion_tokens; OpenAI-compatible servers
        // (Ollama, vLLM, LM Studio, older gateways) still expect max_tokens. Pick by host so both work.
        body[_officialEndpoint ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens;

        return JsonSerializer.Serialize(body, Json);
    }

    public string PreviewRequest(LlmRequest request) => BuildRequestJson(request);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        string body = BuildRequestJson(request);

        for (int attempt = 0; ; attempt++)
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (_auth == AuthStyle.AzureApiKey)
                httpReq.Headers.TryAddWithoutValidation("api-key", _apiKey);
            else
                httpReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
            string responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return Parse(responseBody);

            int status = (int)resp.StatusCode;
            if (attempt < MaxRetries && Array.IndexOf(RetryableStatuses, status) >= 0)
            {
                await Task.Delay(RetryPolicy.Delay(resp, attempt, RetryBaseDelay), ct).ConfigureAwait(false);
                continue;
            }

            throw new LlmApiException(status, $"{Name} API {status}: {Truncate(responseBody, 500)}");
        }
    }

    internal static LlmResponse Parse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    sb.Append(content.GetString());
                }
            }
        }

        var usage = new LlmUsage();
        if (root.TryGetProperty("usage", out var u))
        {
            // Server-side prompt caching is reported as a breakdown of the prompt tokens, so cached
            // reads are subtracted out to keep InputTokens meaning "tokens actually billed as input",
            // matching how the Anthropic client reports it.
            int prompt = GetInt(u, "prompt_tokens");
            int cachedRead = 0;
            if (u.TryGetProperty("prompt_tokens_details", out var details))
                cachedRead = GetInt(details, "cached_tokens");

            usage = new LlmUsage(
                InputTokens: Math.Max(0, prompt - cachedRead),
                OutputTokens: GetInt(u, "completion_tokens"),
                CacheReadInputTokens: cachedRead,
                CacheCreationInputTokens: 0);
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
