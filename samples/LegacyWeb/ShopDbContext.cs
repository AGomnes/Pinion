using System.Data.Entity; // EF6 namespace (EF Core lives under Microsoft.EntityFrameworkCore)

namespace LegacyWeb;

public class ShopDbContext : DbContext
{
    public DbSet<object> Orders { get; set; } = null!;

    public int CountOpenOrders(bool includeArchived)
    {
        int count = 0;
        foreach (var _ in Orders)
        {
            count++;
            if (!includeArchived && count > 1000) break;
        }
        return count;
    }
}
