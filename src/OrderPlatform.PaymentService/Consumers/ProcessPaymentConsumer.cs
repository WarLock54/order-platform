using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using OrderPlatform.Contracts;
using OrderPlatform.PaymentService.Infrastructure;

namespace OrderPlatform.PaymentService.Consumers;

/// <summary>
/// Proje 1'deki payment-service/internal/server.go ProcessPayment ile aynı
/// idempotency garantisi, burada Redis yerine doğrudan Postgres UNIQUE
/// constraint ile sağlanıyor: aynı idempotency_key ile INSERT denemesi
/// ikinci kez yapılırsa Postgres 23505 (unique_violation) hatası fırlatır,
/// biz de bunu yakalayıp önceki sonucu (yeniden ödeme almadan) döneriz.
/// </summary>
public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    private const string UniqueViolation = "23505";

    private readonly PaymentDbContext _db;
    private readonly ILogger<ProcessPaymentConsumer> _logger;

    public ProcessPaymentConsumer(PaymentDbContext db, ILogger<ProcessPaymentConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var msg = context.Message;

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = msg.OrderId,
            CustomerId = msg.CustomerId,
            AmountCents = msg.AmountCents,
            Currency = msg.Currency,
            Status = "SUCCEEDED", // Bu demo'da ödeme her zaman başarılı simüle edilir (bkz. Proje 1'deki aynı basitleştirme).
            IdempotencyKey = msg.IdempotencyKey,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Payments.Add(payment);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            _logger.LogInformation("Ödeme zaten işlenmiş, tekrar alınmıyor: {IdempotencyKey}", msg.IdempotencyKey);

            var existing = await _db.Payments
                .AsNoTracking()
                .SingleAsync(p => p.IdempotencyKey == msg.IdempotencyKey);

            await context.Publish(new PaymentProcessed(msg.OrderId, existing.Id.ToString()));
            return;
        }

        await context.Publish(new PaymentProcessed(msg.OrderId, payment.Id.ToString()));
    }
}
