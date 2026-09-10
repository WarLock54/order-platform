using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderPlatform.Contracts;
using OrderPlatform.OrderService.Infrastructure;

namespace OrderPlatform.OrderService.Consumers;

public class OrderIdempotencyRecorder :
    IConsumer<OrderCompleted>,
    IConsumer<OrderFailed>
{
    private readonly OrderDbContext _db;
    private readonly ILogger<OrderIdempotencyRecorder> _logger;

    public OrderIdempotencyRecorder(OrderDbContext db, ILogger<OrderIdempotencyRecorder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<OrderCompleted> context)
    {
        return RecordAsync(context.Message.OrderId, context.Message.IdempotencyKey, "PAID");
    }

    public Task Consume(ConsumeContext<OrderFailed> context)
    {
        return RecordAsync(context.Message.OrderId, context.Message.IdempotencyKey, "FAILED");
    }

    private async Task RecordAsync(Guid orderId, string idempotencyKey, string finalStatus)
    {
        var exists = await _db.IdempotencyRecords.AnyAsync(x => x.IdempotencyKey == idempotencyKey);
        if (exists)
        {
            _logger.LogInformation("Idempotency kaydı zaten mevcut, tekrar yazılmıyor: {IdempotencyKey}", idempotencyKey);
            return;
        }

        _db.IdempotencyRecords.Add(new OrderIdempotencyRecord
        {
            IdempotencyKey = idempotencyKey,
            OrderId = orderId,
            FinalStatus = finalStatus,
            RecordedAt = DateTime.UtcNow,
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _logger.LogInformation("Idempotency kaydı eşzamanlı olarak zaten yazılmış: {IdempotencyKey}", idempotencyKey);
        }
    }
}