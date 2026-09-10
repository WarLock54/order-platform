using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderPlatform.Contracts;
using OrderPlatform.InventoryService.Infrastructure;

namespace OrderPlatform.InventoryService.Consumers;

/// <summary>
/// Proje 1'deki Commit fonksiyonuyla aynı: koşulsuz bir durum güncellemesi
/// olduğu için doğal olarak idempotent'tir -- aynı ReservationId ile tekrar
/// çağrılması (örn. Saga recovery/redelivery sırasında) zararsızdır.
/// </summary>
public class CommitStockConsumer : IConsumer<CommitStock>
{
    private readonly InventoryDbContext _db;

    public CommitStockConsumer(InventoryDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<CommitStock> context)
    {
        var reservations = await _db.Reservations
            .Where(r => r.ReservationId == context.Message.ReservationId)
            .ToListAsync();

        foreach (var r in reservations)
            r.Status = "COMMITTED";

        await _db.SaveChangesAsync();
        await context.Publish(new StockCommitted(context.Message.OrderId));
    }
}
