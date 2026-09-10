namespace OrderPlatform.Contracts;

/// <summary>inventory-service'in ReserveStock'a verdiği başarılı yanıt.</summary>
public record StockReserved(Guid OrderId, string ReservationId);

/// <summary>inventory-service'in ReserveStock'a verdiği başarısız yanıt.</summary>
public record StockReservationFailed(Guid OrderId, List<string> InsufficientProductIds);

/// <summary>payment-service'in ProcessPayment'a verdiği başarılı yanıt.</summary>
public record PaymentProcessed(Guid OrderId, string PaymentId);

/// <summary>payment-service'in ProcessPayment'a verdiği başarısız yanıt.</summary>
public record PaymentFailed(Guid OrderId, string Reason);

/// <summary>inventory-service'in CommitStock'a verdiği yanıt.</summary>
public record StockCommitted(Guid OrderId);

/// <summary>inventory-service'in ReleaseStock'a (compensating) verdiği yanıt.</summary>
public record StockReleased(Guid OrderId);

/// <summary>
/// Saga tamamlandığında (Completed state) yayınlanır; notification-service
/// bunu dinleyip "bildirim" basar (bkz. Proje 1'deki notification-consumer
/// ile aynı rol). IdempotencyKey, OrderIdempotencyRecorder'ın kalıcı
/// idempotency defterine yazabilmesi için taşınıyor -- Saga instance'ının
/// kendisi tamamlandıktan sonra silindiği için, bu event bilgiyi
/// "hayatta tutan" tek yerdir.
/// </summary>
public record OrderCompleted(Guid OrderId, string IdempotencyKey, string PaymentId, long TotalCents);

/// <summary>Saga başarısız bittiğinde (Failed state) yayınlanır.</summary>
public record OrderFailed(Guid OrderId, string IdempotencyKey, string Reason);