namespace OrderPlatform.OrderService.Infrastructure;

/// <summary>
/// CQRS'in okuma tarafı: yazma modeli (OrderSagaState, PostgreSQL'de) her
/// zaman doğruluk kaynağıdır, ama GET /orders/{id} gibi sorgular Postgres'e
/// gitmek yerine bu Redis'teki denormalize edilmiş görünümden okur. Bu,
/// Proje 1'deki gibi "yazma-yoğun path'i okuma trafiğinden izole etme"
/// prensibinin CQRS karşılığıdır.
///
/// Senkronizasyon: OrderStatusProjector (bkz. Consumers) her Saga event'ini
/// dinleyip bu görünümü günceller -- yani Redis her zaman "sonunda tutarlı"
/// (eventually consistent), asla anlık olarak Postgres ile birebir aynı
/// olması garanti edilmez. Bu, gerçek CQRS sistemlerinde kabul edilen bir
/// trade-off'tur (bkz. README, "Bilinçli Olarak Eksik Bırakılanlar").
/// </summary>
public record OrderReadModel(
    Guid OrderId,
    Guid CustomerId,
    string Status,
    string? FailureReason,
    long TotalCents,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
