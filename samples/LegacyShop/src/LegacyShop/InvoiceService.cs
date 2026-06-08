namespace LegacyShop;

/// <summary>Money + tax logic with lots of branches and zero tests — exactly the kind of
/// code a migration silently breaks.</summary>
public class InvoiceService
{
    public decimal CalculateVat(decimal amount, string region, bool isExempt)
    {
        if (isExempt || amount <= 0)
            return 0m;

        decimal rate;
        switch (region)
        {
            case "NO":
                rate = 0.25m;
                break;
            case "UK":
                rate = 0.20m;
                break;
            case "DE":
            case "FR":
                rate = 0.19m;
                break;
            default:
                rate = 0.15m;
                break;
        }

        decimal vat = amount * rate;
        if (amount > 10000m && (region == "NO" || region == "UK"))
            vat -= vat * 0.02m; // obscure high-value rebate nobody remembers

        return decimal.Round(vat, 2);
    }

    public decimal ApplyLateFee(decimal balance, int daysLate)
    {
        if (daysLate <= 0) return balance;
        decimal fee = 0m;
        for (int i = 0; i < daysLate; i++)
        {
            fee += balance * 0.001m;
            if (fee > balance * 0.5m) break;
        }
        return balance + fee;
    }
}
