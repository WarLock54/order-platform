using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderPlatform.InventoryService.Infrastructure;

/// <summary>Bkz. OrderDbContextFactory'deki aynı gerekçe.</summary>
public class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=inventory;Username=order_platform;Password=order_platform";

        var optionsBuilder = new DbContextOptionsBuilder<InventoryDbContext>();
        optionsBuilder.UseNpgsql(connStr);

        return new InventoryDbContext(optionsBuilder.Options);
    }
}