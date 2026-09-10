using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using OrderPlatform.OrderService.Saga;

namespace OrderPlatform.OrderService.Infrastructure;

/// <summary>
/// Tek bir DbContext hem Saga state'ini hem Transactional Outbox tablolarını
/// (OutboxMessage, OutboxState, InboxState) barındırır. Bu bilinçli bir
/// tasarım kararı: Saga state'inin güncellenmesi VE yeni command'ların
/// outbox'a yazılması AYNI SaveChangesAsync() çağrısında, dolayısıyla aynı
/// veritabanı transaction'ında gerçekleşir -- Proje 1'in README'sinde
/// "Bilinçli Olarak Eksik Bırakılanlar" altında açıkça belirttiğimiz
/// Transactional Outbox Pattern eksikliğinin tam çözümü budur.
/// </summary>
public class OrderDbContext : SagaDbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    // Kalıcı idempotency defteri (bkz. OrderIdempotencyRecord.cs) --
    // OrderSagaState'ten farklı olarak bu satırlar sipariş tamamlansa
    // bile ASLA silinmez.
    public DbSet<OrderIdempotencyRecord> IdempotencyRecords => Set<OrderIdempotencyRecord>();

    protected override IEnumerable<ISagaClassMap> Configurations
    {
        get { yield return new OrderSagaStateMap(); }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ConfigureOrderIdempotencyRecord();

        // MassTransit'in EF Core Outbox implementasyonunun ihtiyaç duyduğu
        // üç tablo. AddEntityFrameworkOutbox<OrderDbContext>() (bkz.
        // Program.cs) bu tabloları kullanarak mesajları atomik şekilde
        // yazıp, arka planda ayrı bir "bus outbox delivery service" ile
        // RabbitMQ'ya güvenilir şekilde publish eder.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}

/// <summary>
/// OrderSagaState için EF Core entity konfigürasyonu. IdempotencyKey
/// üzerindeki UNIQUE index, Proje 1'deki orders.idempotency_key UNIQUE
/// index'iyle birebir aynı garantiyi veritabanı seviyesinde sağlar: aynı
/// key ile eşzamanlı iki SubmitOrder isteği gelse bile ikinci saga
/// instance'ı oluşturulamaz.
/// </summary>
public class OrderSagaStateMap : SagaClassMap<OrderSagaState>
{
    protected override void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<OrderSagaState> entity, ModelBuilder model)
    {
        entity.Property(x => x.CurrentState).HasMaxLength(64);
        entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.FailureReason).HasMaxLength(200);
        entity.Property(x => x.ReservationId).HasMaxLength(100);
        entity.Property(x => x.PaymentId).HasMaxLength(100);

        entity.HasIndex(x => x.IdempotencyKey).IsUnique();

        // EF Core Saga Repository'nin optimistic concurrency için kullandığı
        // satır versiyonu, Postgres'in "xmin" sistem kolonuna otomatik
        // eşlenir (Npgsql 7.0+ standart yolu; eski UseXminAsConcurrencyToken()
        // kaldırıldığı için artık uint property + IsRowVersion() kullanılıyor).
        entity.Property(x => x.RowVersion).IsRowVersion();
    }
}