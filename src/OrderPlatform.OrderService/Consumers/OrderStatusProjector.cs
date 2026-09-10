using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using OrderPlatform.Contracts;
using StackExchange.Redis;

namespace OrderPlatform.OrderService.Consumers;

/// <summary>
/// Bu consumer'lar, OrderStateMachine'in yayınladığı event'lerin AYNI
/// zamanda ikinci bir tüketicisidir (Saga zaten kendi state'ini güncelliyor,
/// bu consumer'lar PARALEL olarak Redis'teki okuma modelini günceller).
/// Bu, CQRS'in "yazma ve okuma modelleri event'ler aracılığıyla senkronize
/// edilir" prensibinin somut halidir.
/// </summary>
public class OrderStatusProjector :
    IConsumer<SubmitOrder>,
    IConsumer<StockReserved>,
    IConsumer<OrderCompleted>,
    IConsumer<OrderFailed>
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<OrderStatusProjector> _logger;

    private static string Key(Guid orderId) => $"order-read-model:{orderId}";

    public OrderStatusProjector(IConnectionMultiplexer redis, ILogger<OrderStatusProjector> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SubmitOrder> context)
    {
        var model = new OrderPlatform.OrderService.Infrastructure.OrderReadModel(
            context.Message.OrderId,
            context.Message.CustomerId,
            "PENDING",
            null,
            context.Message.Items.Sum(i => i.UnitPriceCents * i.Quantity),
            DateTime.UtcNow,
            DateTime.UtcNow);

        await SaveAsync(model);
    }

    public async Task Consume(ConsumeContext<StockReserved> context)
    {
        await UpdateStatusAsync(context.Message.OrderId, "RESERVED", null);
    }

    public async Task Consume(ConsumeContext<OrderCompleted> context)
    {
        await UpdateStatusAsync(context.Message.OrderId, "PAID", null);
    }

    public async Task Consume(ConsumeContext<OrderFailed> context)
    {
        await UpdateStatusAsync(context.Message.OrderId, "FAILED", context.Message.Reason);
    }

    private async Task UpdateStatusAsync(Guid orderId, string status, string? failureReason)
    {
        var db = _redis.GetDatabase();
        var existingJson = await db.StringGetAsync(Key(orderId));
        if (existingJson.IsNullOrEmpty)
        {
            _logger.LogWarning("Read model bulunamadı, güncelleme atlanıyor: {OrderId}", orderId);
            return;
        }

        var existing = JsonSerializer.Deserialize<OrderPlatform.OrderService.Infrastructure.OrderReadModel>(existingJson!)!;
        var updated = existing with { Status = status, FailureReason = failureReason, UpdatedAt = DateTime.UtcNow };
        await SaveAsync(updated);
    }

    private async Task SaveAsync(OrderPlatform.OrderService.Infrastructure.OrderReadModel model)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(model);
        // TTL yok (siparişler kalıcı kalmalı); prod'da arşivleme politikası eklenmeli.
        await db.StringSetAsync(Key(model.OrderId), json);
    }
}
