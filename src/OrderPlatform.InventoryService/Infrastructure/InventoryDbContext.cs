using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace OrderPlatform.InventoryService.Infrastructure;

public class StockItem
{
    public string ProductId { get; set; } = null!;
    public int AvailableQuantity { get; set; }
}

/// <summary>
/// Proje 1'deki "reservations" tablosuyla birebir aynı rol: ReservationId
/// = OrderId (1-1 eşleme, basitlik için -- production'da ayrı bir UUID
/// önerilir, bkz. Proje 1'deki aynı yorum).
/// </summary>
public class Reservation
{
    public int Id { get; set; }
    public string ReservationId { get; set; } = null!;
    public string ProductId { get; set; } = null!;
    public int Quantity { get; set; }
    public string Status { get; set; } = null!; // RESERVED, COMMITTED, RELEASED
}

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options)
    {
    }

    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockItem>(b =>
        {
            b.ToTable("stock_items");
            b.HasKey(x => x.ProductId);
            b.Property(x => x.ProductId).HasColumnName("product_id");
            b.Property(x => x.AvailableQuantity).HasColumnName("available_quantity");
        });

        modelBuilder.Entity<Reservation>(b =>
        {
            b.ToTable("reservations");
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ReservationId).HasColumnName("reservation_id");
            b.Property(x => x.ProductId).HasColumnName("product_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.Status).HasColumnName("status");
            b.HasIndex(x => x.ReservationId);
        });

        // Bu servisin kendi publish ettiği event'ler (StockReserved,
        // StockCommitted, StockReleased) için Transactional Outbox tabloları.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}