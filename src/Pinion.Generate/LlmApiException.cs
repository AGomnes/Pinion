namespace Pinion.Generate;

/// <summary>An error returned by the LLM HTTP endpoint, carrying the status so callers can
/// decide whether to stop the whole run (auth/quota) rather than keep spending.</summary>
public sealed class LlmApiException : Exception
{
    public int StatusCode { get; }

    public LlmApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    /// <summary>Auth/permission/rate-limit — keep going would just waste calls/quota.</summary>
    public bool ShouldAbortRun => StatusCode is 401 or 403 or 429;
}
