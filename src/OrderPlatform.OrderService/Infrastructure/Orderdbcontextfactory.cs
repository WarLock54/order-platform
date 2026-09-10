using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderPlatform.OrderService.Infrastructure;

/// <summary>
/// `dotnet ef migrations add` / `dotnet ef database update` komutları,
/// DbContext'i bulmak için normalde uygulamanın tüm DI container'ını
/// (Program.cs) ayağa kaldırmayı dener. Bizim Program.cs'imiz başlangıçta
/// gerçek Redis/RabbitMQ'ya bağlanmaya çalıştığı için (bkz.
/// ConnectionMultiplexer.Connect), bu design-time modda başarısız olur.
///
/// Bu factory, EF tooling'e DbContext'i DOĞRUDAN, uygulamanın geri kalanını
/// hiç başlatmadan nasıl oluşturacağını söyler -- migration'lar için
/// localhost bağlantı dizesi kullanılır çünkü migration'lar host
/// makineden (Docker container'ının DIŞINDAN), Docker'ın dışa açtığı
/// portlar üzerinden çalıştırılır.
/// </summary>
public class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=orders;Username=order_platform;Password=order_platform";

        var optionsBuilder = new DbContextOptionsBuilder<OrderDbContext>();
        optionsBuilder.UseNpgsql(connStr);

        return new OrderDbContext(optionsBuilder.Options);
    }
}