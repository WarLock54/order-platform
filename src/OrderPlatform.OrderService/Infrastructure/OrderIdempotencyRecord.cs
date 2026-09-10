using Microsoft.EntityFrameworkCore;

namespace OrderPlatform.OrderService.Infrastructure;

/// <summary>
/// MassTransit'in EF Saga Repository'si, SetCompletedWhenFinalized()
/// çağrıldığı için tamamlanan saga instance'larını (OrderSagaState
/// tablosundan) SİLER -- bu, yüksek hacimli sistemlerde performans için
/// doğru bir davranıştır, ama bunun bir sonucu var: OrderSagaState
/// üzerindeki IdempotencyKey UNIQUE index koruması, sipariş TAMAMLANDIKTAN
/// SONRA ortadan kalkar (satır artık yok).
///
/// Bu tablo, o boşluğu kapatır: sipariş tamamlandığında/başarısız
/// olduğunda (bkz. OrderIdempotencyRecorder consumer) buraya KALICI bir
/// kayıt yazılır. POST /orders, yeni bir SubmitOrder yayınlamadan ÖNCE bu
/// tabloyu kontrol eder -- Proje 1'deki "orders.idempotency_key UNIQUE
/// index + FindByIdempotencyKey" deseninin burada, Saga'nın ephemeral
/// yaşam döngüsünden bağımsız kalıcı karşılığıdır.
/// </summary>
public class OrderIdempotencyRecord
{
    public string IdempotencyKey { get; set; } = null!;
    public Guid OrderId { get; set; }
    public string FinalStatus { get; set; } = null!; // PAID, FAILED
    public DateTime RecordedAt { get; set; }
}

public static class OrderIdempotencyRecordModelBuilderExtensions
{
    public static void ConfigureOrderIdempotencyRecord(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderIdempotencyRecord>(b =>
        {
            b.ToTable("order_idempotency_records");
            b.HasKey(x => x.IdempotencyKey);
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200);
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.FinalStatus).HasColumnName("final_status").HasMaxLength(50);
            b.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        });
    }
}