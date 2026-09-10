using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace OrderPlatform.PaymentService.Infrastructure;

public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public long AmountCents { get; set; }
    public string Currency { get; set; } = null!;
    public string Status { get; set; } = null!; // SUCCEEDED, FAILED
    public string IdempotencyKey { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
    {
    }

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.AmountCents).HasColumnName("amount_cents");
            b.Property(x => x.Currency).HasColumnName("currency");
            b.Property(x => x.Status).HasColumnName("status");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");

            // Proje 1'deki idempotency garantisinin veritabanı seviyesindeki
            // karşılığı: aynı idempotency_key ile ikinci bir INSERT unique
            // constraint ihlaliyle reddedilir (bkz. ProcessPaymentConsumer).
            b.HasIndex(x => x.IdempotencyKey).IsUnique();
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}