namespace OrderPlatform.Contracts;

// Command'lar: "şunu yap" anlamına gelir, tam olarak bir tüketicisi vardır.
// Event'ler (Events.cs) ise "şu oldu" anlamına gelir, sıfır ya da daha çok
// dinleyicisi olabilir. Bu ayrım MassTransit/Saga tasarımında önemlidir:
// Saga, command yayınlar (PublishAsync ile, ama mantıksal olarak tek
// alıcıya yöneliktir); worker'lar event yayınlayarak Saga'yı bilgilendirir.

public record OrderLineItem(string ProductId, int Quantity, long UnitPriceCents);

/// <summary>
/// order-service API'sine POST /orders ile gelen isteğin Saga'yı başlatan
/// command'i. IdempotencyKey, Proje 1'deki aynı garantiyi sağlar: aynı key
/// ile tekrar gönderilen istek yeni bir Saga başlatmaz (bkz. OrderStateMachine
/// ve OrderSagaState.IdempotencyKey üzerindeki UNIQUE index).
/// </summary>
public record SubmitOrder(Guid OrderId, Guid CustomerId, List<OrderLineItem> Items, string IdempotencyKey);

/// <summary>inventory-service'e gönderilen stok rezervasyon komutu.</summary>
public record ReserveStock(Guid OrderId, List<OrderLineItem> Items, string IdempotencyKey);

/// <summary>Ödeme başarılı olduktan sonra rezervasyonu kalıcı hale getirme komutu.</summary>
public record CommitStock(Guid OrderId, string ReservationId);

/// <summary>Ödeme başarısız olduğunda rezervasyonu geri alma (compensating) komutu.</summary>
public record ReleaseStock(Guid OrderId, string ReservationId, string Reason);

/// <summary>payment-service'e gönderilen ödeme işleme komutu.</summary>
public record ProcessPayment(Guid OrderId, Guid CustomerId, long AmountCents, string Currency, string IdempotencyKey);
