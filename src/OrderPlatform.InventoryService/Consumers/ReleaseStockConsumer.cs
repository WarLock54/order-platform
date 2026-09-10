using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderPlatform.Contracts;
using OrderPlatform.InventoryService.Infrastructure;

namespace OrderPlatform.InventoryService.Consumers;

/// <summary>
/// Compensating transaction: yalnızca hâlâ "RESERVED" durumundaki
/// rezervasyonları geri alır (WHERE status = 'RESERVED'). Bu filtre sayesinde
/// aynı ReservationId ile tekrar çağrılması stoğu iki kez iade etmez --
/// ilk çağrıdan sonra status zaten "RELEASED" olduğu için ikinci çağrı
/// hiçbir satır bulamaz ve no-op olur.
/// </summary>
public class ReleaseStockConsumer : IConsumer<ReleaseStock>
{
    private readonly InventoryDbContext _db;

    public ReleaseStockConsumer(InventoryDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ReleaseStock> context)
    {
        var reservations = await _db.Reservations
            .Where(r => r.ReservationId == context.Message.ReservationId && r.Status == "RESERVED")
            .ToListAsync();

        foreach (var r in reservations)
        {
            var stock = await _db.StockItems.FindAsync(r.ProductId);
            if (stock is not null)
                stock.AvailableQuantity += r.Quantity;

            r.Status = "RELEASED";
        }

        await _db.SaveChangesAsync();
        await context.Publish(new StockReleased(context.Message.OrderId));
    }
}
