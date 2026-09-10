using MassTransit;
using Microsoft.Extensions.Logging;
using OrderPlatform.Contracts;

namespace OrderPlatform.NotificationService.Consumers;

/// <summary>Proje 1'deki notification-consumer ile aynı rol: Saga'nın
/// yayınladığı nihai event'leri dinleyip "bildirim" basar.</summary>
public class OrderCompletedConsumer : IConsumer<OrderCompleted>
{
    private readonly ILogger<OrderCompletedConsumer> _logger;

    public OrderCompletedConsumer(ILogger<OrderCompletedConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<OrderCompleted> context)
    {
        _logger.LogInformation(
            "[BİLDİRİM] Siparişiniz onaylandı: order_id={OrderId} payment_id={PaymentId} total_cents={TotalCents}",
            context.Message.OrderId, context.Message.PaymentId, context.Message.TotalCents);
        return Task.CompletedTask;
    }
}

public class OrderFailedConsumer : IConsumer<OrderFailed>
{
    private readonly ILogger<OrderFailedConsumer> _logger;

    public OrderFailedConsumer(ILogger<OrderFailedConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<OrderFailed> context)
    {
        _logger.LogWarning(
            "[BİLDİRİM] Siparişiniz başarısız oldu: order_id={OrderId} reason={Reason}",
            context.Message.OrderId, context.Message.Reason);
        return Task.CompletedTask;
    }
}
