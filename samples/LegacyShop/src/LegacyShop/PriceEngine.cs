namespace LegacyShop;

public record CartLine(string Sku, int Quantity, decimal UnitPrice);

/// <summary>A big, branchy pricing method — the "500-line method nobody dares touch", in miniature.</summary>
public class PriceEngine
{
    public decimal ApplyDiscounts(IReadOnlyList<CartLine> lines, string? coupon, bool isMember)
    {
        decimal total = 0m;
        foreach (var line in lines)
        {
            decimal lineTotal = line.UnitPrice * line.Quantity;

            if (line.Quantity >= 100) lineTotal *= 0.85m;
            else if (line.Quantity >= 50) lineTotal *= 0.90m;
            else if (line.Quantity >= 10) lineTotal *= 0.95m;

            if (line.Sku.StartsWith("CLEARANCE") && line.UnitPrice > 0)
                lineTotal *= 0.5m;

            total += lineTotal;
        }

        if (isMember && total > 0)
            total *= 0.95m;

        if (!string.IsNullOrEmpty(coupon))
        {
            switch (coupon.ToUpperInvariant())
            {
                case "SAVE10":
                    total *= 0.90m;
                    break;
                case "SAVE20" when total > 500m:
                    total *= 0.80m;
                    break;
                case "FREESHIP":
                    total -= 9.99m;
                    break;
            }
        }

        return total < 0 ? 0 : decimal.Round(total, 2);
    }
}
