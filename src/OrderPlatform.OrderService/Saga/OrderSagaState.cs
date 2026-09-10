using MassTransit;

namespace OrderPlatform.OrderService.Saga;

/// <summary>
/// OrderStateMachine'in her sipariş için tuttuğu kalıcı durum. MassTransit'in
/// EF Core Saga Repository'si bu sınıfı otomatik olarak PostgreSQL'e
/// yazar/okur -- Proje 1'de elle yazdığımız saga_steps tablosunun ve
/// recovery.go'nun yaptığı işi burada framework üstleniyor:
///   - CurrentState alanı, hangi adımda kalındığını kalıcı tutar.
///   - RowVersion, EF'in optimistic concurrency kontrolü için gereklidir
///     (aynı saga'ya eşzamanlı iki mesaj geldiğinde çakışmayı yönetir).
///   - Servis crash olup yeniden başladığında, MassTransit'in kendi
///     redelivery/retry mekanizması + bu kalıcı state sayesinde Saga
///     kaldığı state'ten devam eder; Proje 1'deki gibi elle bir
///     "RecoverIncompleteOrders" fonksiyonu yazmaya gerek YOKTUR.
/// </summary>
public class OrderSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; } // = OrderId

    public string CurrentState { get; set; } = null!;

    public Guid CustomerId { get; set; }

    public long TotalCents { get; set; }

    public string? ReservationId { get; set; }

    public string? PaymentId { get; set; }

    /// <summary>
    /// Proje 1'deki idempotency_key ile birebir aynı rol: aynı key ile
    /// tekrar gelen SubmitOrder komutu (bkz. OrderStateMachine
    /// Event(() => SubmitOrderEvent, x => x.CorrelateById(...))) yeni bir
    /// saga instance'ı oluşturmaz -- UNIQUE index (bkz. OrderDbContext)
    /// bunu veritabanı seviyesinde garanti eder.
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;

    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // EF Core Saga Repository'nin optimistic concurrency için kullandığı
    // satır versiyonu. Postgres'in "xmin" sistem kolonuna eşlenir --
    // elle bir kolon eklemeye/migration yazmaya gerek yok (bkz.
    // OrderDbContext.cs'teki IsRowVersion() konfigürasyonu). Npgsql 7.0+
    // ile bu artık uint tipinde bir property + IsRowVersion() ile
    // yapılıyor (eski UseXminAsConcurrencyToken() kaldırıldı).
    public uint RowVersion { get; set; }
}