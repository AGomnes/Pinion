using System;

namespace LegacyFramework
{
    // Old-school C# (no namespaces-as-statements, no nullable) — the kind of code that
    // has shipped for a decade with no tests.
    public class LegacyTaxCalculator
    {
        public decimal ComputeTax(decimal amount, string bracket)
        {
            if (amount <= 0)
                return 0m;

            decimal rate;
            if (bracket == "high")
                rate = 0.40m;
            else if (bracket == "mid")
                rate = 0.25m;
            else if (bracket == "low")
                rate = 0.10m;
            else
                rate = 0.20m;

            decimal tax = amount * rate;
            if (amount > 100000m && (bracket == "high" || bracket == "mid"))
                tax += amount * 0.01m;

            return Math.Round(tax, 2);
        }

        public bool IsValidBracket(string bracket)
        {
            return bracket == "high" || bracket == "mid" || bracket == "low";
        }
    }
}
