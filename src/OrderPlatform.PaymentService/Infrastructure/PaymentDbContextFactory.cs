using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderPlatform.PaymentService.Infrastructure;

/// <summary>Bkz. OrderDbContextFactory'deki aynı gerekçe.</summary>
public class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=payments;Username=order_platform;Password=order_platform";

        var optionsBuilder = new DbContextOptionsBuilder<PaymentDbContext>();
        optionsBuilder.UseNpgsql(connStr);

        return new PaymentDbContext(optionsBuilder.Options);
    }
}