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

    // Takes a CONCRETE List<int> — an int[] literal isn't assignable to it, so the synthesizer must
    // emit a List initializer, not `new[] { … }` (found dogfooding nopCommerce).
    public int SumList(System.Collections.Generic.List<int> values)
    {
        int s = 0;
        foreach (var v in values) s += v;
        return s;
    }

    public bool TryDouble(string text, out int value)
    {
        if (int.TryParse(text, out int parsed)) { value = parsed * 2; return true; }
        value = 0;
        return false;
    }

    public void Bump(ref int counter, int by) => counter += by;

    // Non-deterministic: reads the wall clock, so its captured value differs every run. The generator
    // must NOT ship this as a golden master — it should detect the flake on the confirm run and quarantine
    // it with a "needs a seam (DateTime.Now)" diagnosis rather than emit a snapshot that fails every verify.
    public long TicksNow() => System.DateTime.Now.Ticks;

    public T Echo<T>(T input) => input; // generic — deterministic generator should skip cleanly

    public int Combine(int a, int b) => a + b;            // overload 1
    public int Combine(int a) => a;                        // overload 2 (same DisplayName)

    public ResultBox Wrap(int n) => new(n >= 0 ? n : (int?)null); // returns a throwing-getter wrapper

    // Takes an IFormatProvider — the dominant real-world "interface param" the synthesizer must fill
    // with a real culture (not default→null) to capture real formatting behavior.
    public string ToStringInvariant(decimal amount, System.IFormatProvider provider) =>
        amount.ToString("C", provider);

    // ---- Tier-1 regression shapes: each previously emitted a characterization test that didn't compile ----

    /// <summary>float parameter + a mined float constant — inputs must be `f`-suffixed, not `0d`/`1.5d`.</summary>
    public float Scale(float factor) => factor < 1.5f ? factor : factor * 2f;

    /// <summary>Branches on a string constant containing a newline — the mined literal must be escaped (CS1010).</summary>
    public string Multiline(string s) => s == "line1\nline2" ? "match" : "other";

    /// <summary>Compares against a non-finite double — the mined constant has no bare literal form (`Infinityd`).</summary>
    public string Classify(double x) => x >= double.PositiveInfinity ? "infinite" : "finite";

    /// <summary>ref string — a `null` candidate must declare the temp with an explicit type, not `var` (CS0815).</summary>
    public void Relabel(ref string label) => label = string.IsNullOrEmpty(label) ? "none" : label.Trim();

    /// <summary>Parameter type has a `required` member — construction needs an object initializer (CS9035).</summary>
    public string Ship(Shipment shipment) => $"{shipment.Destination}:{shipment.Weight}";
}

/// <summary>A type with a <c>required</c> member — <c>new Shipment()</c> without an initializer is CS9035,
/// so the synthesizer must emit <c>new Shipment { Destination = … }</c>.</summary>
public sealed class Shipment
{
    public required string Destination { get; init; }
    public int Weight { get; set; }
}

/// <summary>A service interface with no construction path — the DI-heavy-service case the synthesizer
/// must fill with a generated stub (not null), so PriceQuoter below runs instead of NREing.</summary>
public interface IRate
{
    decimal Multiplier { get; }
    System.Threading.Tasks.Task<decimal> LookupAsync(string sku);
}

/// <summary>Constructor-injects IRate — like a real service. With a stub for IRate, Quote() runs with a
/// no-op collaborator (Multiplier defaults to 0) and records real behavior rather than a null-ref.</summary>
public sealed class PriceQuoter
{
    private readonly IRate _rate;
    public PriceQuoter(IRate rate) => _rate = rate;

    public decimal Quote(decimal basePrice) => basePrice * (1 + _rate.Multiplier);
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
