using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderPlatform.OrderService.Infrastructure;
using StackExchange.Redis;

namespace OrderPlatform.OrderService.Services;

/// <summary>
/// CQRS'in okuma modeli (Redis) "en iyi çaba" (eventually consistent)
/// prensibiyle senkronize tutulur: OrderStatusProjector bir event'i
/// kaçırırsa (örn. geçici Redis kesintisi sırasında consumer hata verip
/// mesaj kaybolursa -- ki MassTransit'in en-az-bir-kez garantisi bunu
/// normalde önler, ama uzun süreli bir Redis kesintisinde retry'lar da
/// tükenip mesaj fault queue'ya düşebilir), Redis'teki görünüm kalıcı
/// olarak yanlış/eksik kalabilir.
///
/// Bu servis, doğruluk kaynağı olan `order_idempotency_records`
/// tablosunu (bkz. OrderIdempotencyRecord.cs) periyodik olarak tarayıp
/// Redis'in bu tablodaki NİHAİ durumlarla (PAID/FAILED) eşleştiğini
/// garanti eder. Sadece TAMAMLANMIŞ siparişleri kapsar; hâlâ devam eden
/// (PENDING/RESERVED) siparişlerin reconciliation'ı bilinçli olarak
/// kapsam dışıdır (bkz. README, "Bilinçli Olarak Eksik Bırakılanlar") --
/// aktif bir Saga'nın anlık durumunu dıştan senkronize etmeye çalışmak,
/// Saga'nın kendi state geçişleriyle yarışan ayrı bir tutarlılık sorunu
/// yaratabilir; oysa TAMAMLANMIŞ bir siparişin nihai durumu artık asla
/// değişmez, bu yüzden onu senkronize etmek güvenlidir.
/// </summary>
public class ReadModelReconciliationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ReadModelReconciliationService> _logger;

    public ReadModelReconciliationService(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        ILogger<ReadModelReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Servis başlar başlamaz değil, ilk turdan önce kısa bir bekleme --
        // uygulamanın diğer bağımlılıklarının (DB, Redis bağlantı havuzu)
        // tam olarak hazır olmasına zaman tanır.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Reconciliation turunun kendisi asla servisi çökertmemeli --
                // bir sonraki turda tekrar denenir.
                _logger.LogError(ex, "Reconciliation turu başarısız oldu, bir sonraki turda tekrar denenecek");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var redisDb = _redis.GetDatabase();

        var terminalRecords = await db.IdempotencyRecords.AsNoTracking().ToListAsync(ct);
        var fixedCount = 0;

        foreach (var record in terminalRecords)
        {
            var key = $"order-read-model:{record.OrderId}";
            var existingJson = await redisDb.StringGetAsync(key);

            if (existingJson.IsNullOrEmpty)
            {
                // Redis'te bu sipariş için HİÇ kayıt yok -- muhtemelen
                // SubmitOrder event'i OrderStatusProjector tarafından hiç
                // işlenememiş. Idempotency defterinden sadece OrderId ve
                // nihai durumu kurtarabiliyoruz; CustomerId/TotalCents gibi
                // alanlar burada mevcut değil (bilinçli bir sınırlama --
                // bu alanları kalıcı olarak saklamak, defterin tek amacını
                // "idempotency kontrolü"nün ötesine taşırdı). Bu durumda
                // eksik alanlar placeholder değerlerle doldurulur.
                var placeholder = new OrderReadModel(
                    record.OrderId, Guid.Empty, record.FinalStatus, null, 0,
                    record.RecordedAt, record.RecordedAt);
                await redisDb.StringSetAsync(key, JsonSerializer.Serialize(placeholder));
                fixedCount++;
                continue;
            }

            var existing = JsonSerializer.Deserialize<OrderReadModel>(existingJson!);
            if (existing is not null && existing.Status != record.FinalStatus)
            {
                // Var olan kaydı KORUYARAK (CustomerId, TotalCents vb. dahil)
                // sadece durumu düzeltiyoruz.
                var patched = existing with { Status = record.FinalStatus, UpdatedAt = DateTime.UtcNow };
                await redisDb.StringSetAsync(key, JsonSerializer.Serialize(patched));
                fixedCount++;
            }
        }

        if (fixedCount > 0)
        {
            _logger.LogWarning(
                "Reconciliation: {Count} sipariş için Redis okuma modeli Postgres ile senkron değildi, düzeltildi",
                fixedCount);
        }
    }
}