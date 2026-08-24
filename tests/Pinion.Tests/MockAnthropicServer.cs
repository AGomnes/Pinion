using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pinion.Tests;

/// <summary>
/// A tiny in-process HTTP server that speaks the Anthropic Messages API wire format, so the REAL
/// <see cref="Pinion.Generate.AnthropicClient"/> can be exercised end-to-end — request serialization,
/// headers, response parsing, error mapping, and the compile→run→repair loop — with no API key and no
/// cost. Point the client at <see cref="BaseUrl"/> via its base-url override (the same hook used for
/// local/air-gapped models). Scripted responses are dequeued per request; received requests are
/// recorded for assertions.
/// </summary>
internal sealed class MockAnthropicServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Queue<(int Status, string Body)> _responses = new();
    private readonly object _gate = new();

    public string BaseUrl { get; }
    public List<Recorded> Requests { get; } = new();

    public sealed record Recorded(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    public MockAnthropicServer()
    {
        int port = FreeTcpPort();
        BaseUrl = $"http://localhost:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Queue a successful Messages response whose single text block is <paramref name="text"/>.</summary>
    public void EnqueueMessage(string text, int inputTokens = 10, int outputTokens = 20,
        int cacheRead = 0, int cacheCreation = 0)
    {
        string body = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-sonnet-4-6",
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                cache_read_input_tokens = cacheRead,
                cache_creation_input_tokens = cacheCreation,
            },
        });
        lock (_gate) _responses.Enqueue((200, body));
    }

    /// <summary>Queue a verbatim body with the given status. Used by the OpenAI-shaped tests, whose
    /// payloads are not Anthropic Messages objects.</summary>
    public void EnqueueRaw(int status, string body)
    {
        lock (_gate) _responses.Enqueue((status, body));
    }

    /// <summary>Queue an API error with the given HTTP status and error type.</summary>
    public void EnqueueError(int status, string errorType = "api_error", string message = "boom")
    {
        string body = System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = new { type = errorType, message } });
        lock (_gate) _responses.Enqueue((status, body));
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }

            string reqBody;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                reqBody = await reader.ReadToEndAsync().ConfigureAwait(false);

            var headers = ctx.Request.Headers.AllKeys
                .Where(k => k is not null)
                .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
            lock (_gate) Requests.Add(new Recorded(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, headers, reqBody));

            (int status, string body) = NextResponse();
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    private (int, string) NextResponse()
    {
        lock (_gate)
            return _responses.Count > 0 ? _responses.Dequeue() : (200, """{"type":"message","content":[{"type":"text","text":""}],"usage":{}}""");
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
