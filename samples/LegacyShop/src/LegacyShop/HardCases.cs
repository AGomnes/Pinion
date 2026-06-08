namespace LegacyShop;

/// <summary>Real-world C# shapes that a naive generator would emit broken tests for.</summary>
public class HardCases
{
    public async Task<int> CountUpAsync(int seed)
    {
        await Task.Yield();
        int n = 0;
        for (int i = 0; i < seed; i++) n += i;
        return n;
    }

    public async Task RunAsync(int n)
    {
        await Task.Yield();
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
    }

    public bool TryDouble(string text, out int value)
    {
        if (int.TryParse(text, out int parsed)) { value = parsed * 2; return true; }
        value = 0;
        return false;
    }

    public void Bump(ref int counter, int by) => counter += by;

    public T Echo<T>(T input) => input; // generic — deterministic generator should skip cleanly

    public int Combine(int a, int b) => a + b;            // overload 1
    public int Combine(int a) => a;                        // overload 2 (same DisplayName)

    public ResultBox Wrap(int n) => new(n >= 0 ? n : (int?)null); // returns a throwing-getter wrapper
}

/// <summary>
/// A façade type obtained via a static factory, not <c>new</c> — the dominant real-world shape
/// (NodaTime's <c>LocalDatePattern.Iso</c>, <c>DateTimeZone.Utc</c>). With a private ctor the
/// generator must recover a receiver from the static property; otherwise it would emit
/// <c>default(Formatter)!</c> (null) and capture only a NullReferenceException.
/// </summary>
public sealed class Formatter
{
    private readonly string _prefix;
    private Formatter(string prefix) => _prefix = prefix;

    public static Formatter Default { get; } = new("#");

    public string Render(int value) => _prefix + value;
}

/// <summary>
/// A result-monad wrapper whose <see cref="Value"/> getter THROWS on failure (the
/// ParseResult&lt;T&gt;/Result&lt;T&gt; shape). Serializing it naively blows up the whole snapshot;
/// the generated test must configure Verify to skip members that throw so the rest of the real state
/// (e.g. <see cref="Success"/>) is still recorded.
/// </summary>
public sealed class ResultBox
{
    private readonly int? _value;
    public ResultBox(int? value) => _value = value;
    public bool Success => _value.HasValue;
    public int Value => _value ?? throw new InvalidOperationException("no value");
}
