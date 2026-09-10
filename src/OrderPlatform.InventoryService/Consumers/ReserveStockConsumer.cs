using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPlatform.Contracts;
using OrderPlatform.InventoryService.Infrastructure;

namespace OrderPlatform.InventoryService.Consumers;

/// <summary>
/// Proje 1'deki inventory-service/internal/repository.go TryReserve
/// fonksiyonuyla birebir aynı iş mantığı: "SELECT ... FOR UPDATE" ile satır
/// kilitleyip tüm kalemler yeterliyse hepsini birden düşürür (all-or-nothing);
/// herhangi biri yetersizse hiçbir düşüm yapmadan geri döner.
/// </summary>
public class ReserveStockConsumer : IConsumer<ReserveStock>
{
    private readonly InventoryDbContext _db;
    private readonly ILogger<ReserveStockConsumer> _logger;

    public ReserveStockConsumer(InventoryDbContext db, ILogger<ReserveStockConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ReserveStock> context)
    {
        var msg = context.Message;
        var reservationId = msg.OrderId.ToString();

        // Idempotency (savunma katmanı): MassTransit'in kendi redelivery'si
        // aynı mesajı tekrar getirirse, burada zaten var olan rezervasyonu
        // tespit edip stoğu ikinci kez düşürmeden aynı sonucu tekrar yayınlıyoruz.
        // Bu, Proje 1'deki Redis tabanlı idempotency_key kontrolünün burada
        // veritabanı sorgusuyla yapılan karşılığıdır.
        var alreadyReserved = await _db.Reservations.AnyAsync(r => r.ReservationId == reservationId);
        if (alreadyReserved)
        {
            _logger.LogInformation("Rezervasyon zaten mevcut, tekrar işlenmiyor: {ReservationId}", reservationId);
            await context.Publish(new StockReserved(msg.OrderId, reservationId));
            return;
        }

        // NOT: Başlangıçta burada IsolationLevel.Serializable kullanılmıştı,
        // ama canlı stres testinde bu, aynı ürüne eşzamanlı erişimlerde
        // Postgres'in "could not serialize access due to concurrent update"
        // (40001) hatasını tetiklediği tespit edildi (bkz. README,
        // "Eşzamanlılık Stres Testi"). Kök neden: SERIALIZABLE, tek satırlık
        // FOR UPDATE kilitlememiz için gereğinden fazla katı bir garanti
        // (predicate locking) getiriyordu.
        //
        // ReadCommitted + FOR UPDATE, bu senaryo için doğru ve yeterli
        // seviyedir: iki eşzamanlı transaction aynı product_id satırını
        // kilitlemeye çalıştığında, ikincisi HATA FIRLATMAK yerine
        // birincisi commit/rollback edene kadar doğal olarak BEKLER, sonra
        // satırın GÜNCEL (commit edilmiş) değerini okur. Bu, hem doğruluğu
        // korur (oversell imkansız) hem de retry'a hiç ihtiyaç bırakmadan,
        // tam throughput ile çalışır.
        await using var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        var insufficientProductIds = new List<string>();
        var lockedStock = new Dictionary<string, StockItem>();

        foreach (var item in msg.Items)
        {
            var stock = await _db.StockItems
                .FromSqlInterpolated($"SELECT * FROM stock_items WHERE product_id = {item.ProductId} FOR UPDATE")
                .SingleOrDefaultAsync();

            if (stock is null || stock.AvailableQuantity < item.Quantity)
            {
                insufficientProductIds.Add(item.ProductId);
                continue;
            }
            lockedStock[item.ProductId] = stock;
        }

        if (insufficientProductIds.Count > 0)
        {
            await tx.RollbackAsync();
            await context.Publish(new StockReservationFailed(msg.OrderId, insufficientProductIds));
            return;
        }

        foreach (var item in msg.Items)
        {
            lockedStock[item.ProductId].AvailableQuantity -= item.Quantity;
            _db.Reservations.Add(new Reservation
            {
                ReservationId = reservationId,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                Status = "RESERVED",
            });
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await context.Publish(new StockReserved(msg.OrderId, reservationId));
    }
}