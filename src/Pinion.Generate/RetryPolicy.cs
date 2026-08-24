using System.Net.Http;

namespace Pinion.Generate;

/// <summary>
/// Backoff shared by the HTTP providers. Extracted so the subtle part — honoring the server's
/// Retry-After, jitter, and the cap — has one implementation rather than one per provider.
/// </summary>
internal static class RetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Server's Retry-After when present, else exponential backoff plus jitter, capped at 30s.</summary>
    public static TimeSpan Delay(HttpResponseMessage resp, int attempt, TimeSpan baseDelay)
    {
        if (resp.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d && d > TimeSpan.Zero) return Cap(d);
            if (ra.Date is { } when && when - DateTimeOffset.UtcNow is { Ticks: > 0 } until) return Cap(until);
        }
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;

        double seconds = baseDelay.TotalSeconds * Math.Pow(2, attempt);
        double jitter = Random.Shared.NextDouble() * 0.5;
        return Cap(TimeSpan.FromSeconds(seconds + jitter));
    }

    private static TimeSpan Cap(TimeSpan t) => t > MaxDelay ? MaxDelay : t;
}
