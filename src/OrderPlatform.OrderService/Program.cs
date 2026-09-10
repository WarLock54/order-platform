using MassTransit;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderPlatform.Contracts;
using OrderPlatform.OrderService.Infrastructure;
using OrderPlatform.OrderService.Saga;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------
// Serilog: C# raporundaki Serilog/NLog karışıklığı (risk #8) burada
// baştan tek standarda (Serilog, JSON çıktı) indirgeniyor -- Proje 1'deki
// log/slog ile aynı rol.
// ---------------------------------------------------------------------
builder.Host.UseSerilog((context, config) =>
{
    config
        .Enrich.WithProperty("service", "order-service")
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter());
});

var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres eksik");
var redisConnStr = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis eksik");
var rabbitMqHost = builder.Configuration["RabbitMq:Host"] ?? "rabbitmq";

// ---------------------------------------------------------------------
// EF Core: Saga state + Transactional Outbox aynı DbContext'te.
// ---------------------------------------------------------------------
builder.Services.AddDbContext<OrderDbContext>(opt => opt.UseNpgsql(postgresConnStr));

// CQRS okuma modeli reconciliation: bkz. ReadModelReconciliationService.cs
// için tam gerekçe. 30 saniyede bir, Redis'in kalıcı idempotency
// defteriyle (doğruluk kaynağı) senkron olduğunu garanti eder.
builder.Services.AddHostedService<OrderPlatform.OrderService.Services.ReadModelReconciliationService>();

// ---------------------------------------------------------------------
// Redis: CQRS okuma modeli için.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnStr));

// ---------------------------------------------------------------------
// MassTransit: Saga State Machine + Transactional Outbox + RabbitMQ.
// ---------------------------------------------------------------------
builder.Services.AddMassTransit(x =>
{
    // Transactional Outbox: OrderDbContext'e yazılan her mesaj, saga
    // state güncellemesiyle AYNI transaction'da commit edilir. Ayrı bir
    // "bus outbox delivery" arka plan servisi, commit edilmiş mesajları
    // periyodik olarak (varsayılan 10sn) RabbitMQ'ya güvenilir şekilde
    // publish eder. Bu, Proje 1'in bilinçli olarak atladığı dual-write
    // sorununu tam olarak çözer.
    x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<OrderDbContext>();
            r.UsePostgres();
        });

    x.AddConsumer<OrderPlatform.OrderService.Consumers.OrderStatusProjector>();
    x.AddConsumer<OrderPlatform.OrderService.Consumers.OrderIdempotencyRecorder>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        // --- Partitioning (PrefetchCount=1'in yerini alıyor) ---
        // MassTransit'in EF Core Saga Repository'si, optimistic concurrency
        // garantisi için satır erişimlerinde dahili olarak SERIALIZABLE
        // izolasyon kullanıyor (bunu biz konfigüre etmiyoruz, framework'ün
        // kendi tasarım kararı). Bu yüzden AYNI sipariş (CorrelationId) için
        // art arda gelen event'lerin (StockReserved, PaymentProcessed, ...)
        // asla paralel işlenmemesi gerekiyor -- aksi halde kendi kendiyle
        // çakışan bir transaction (40001) oluşur.
        //
        // Naif çözüm PrefetchCount=1 idi: TÜM mesajları (farklı siparişler
        // dahil) sıraya soktuğu için doğruydu ama throughput'u öldürüyordu.
        // Doğru çözüm: sadece AYNI OrderId'ye ait mesajları sıraya sokan,
        // FARKLI OrderId'lere ait mesajları ise paralel işleyen bir
        // partitioner. 10 partition, aynı anda en fazla 10 farklı siparişin
        // paralel ilerlemesine izin verirken, tek bir siparişin adımlarının
        // her zaman doğru sırada işlenmesini garanti eder.
        cfg.ReceiveEndpoint("OrderSagaState", e =>
        {
            e.UseMessageRetry(r => r.Exponential(
                retryLimit: 3,
                minInterval: TimeSpan.FromMilliseconds(200),
                maxInterval: TimeSpan.FromSeconds(5),
                intervalDelta: TimeSpan.FromMilliseconds(200)));

            var partitioner = e.CreatePartitioner(10);

            e.ConfigureSaga<OrderSagaState>(context, sagaCfg =>
            {
                sagaCfg.Message<SubmitOrder>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
                sagaCfg.Message<StockReserved>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
                sagaCfg.Message<StockReservationFailed>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
                sagaCfg.Message<PaymentProcessed>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
                sagaCfg.Message<PaymentFailed>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
                sagaCfg.Message<StockCommitted>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
                sagaCfg.Message<StockReleased>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId));
            });
        });

        // Fault queue / retry policy: C# raporunun Hangfire job'larında
        // eksik olduğunu belirttiği dead-letter/retry disiplini (risk #6)
        // burada baştan standart olarak kuruluyor. Bu, diğer (Saga
        // olmayan) endpoint'lere (OrderStatusProjector, OrderIdempotency
        // Recorder) uygulanır -- OrderSagaState kendi retry'ını yukarıda
        // zaten tanımladı.
        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 3,
            minInterval: TimeSpan.FromMilliseconds(200),
            maxInterval: TimeSpan.FromSeconds(5),
            intervalDelta: TimeSpan.FromMilliseconds(200)));

        cfg.ConfigureEndpoints(context);
    });
});

// ---------------------------------------------------------------------
// Health Checks: C# raporunun eksik bulduğu (risk #8) gözlemlenebilirlik
// katmanı. Bu endpoint'ler K8s liveness/readiness probe'ları tarafından
// sorgulanabilir (bkz. Proje 1'deki pkg/health'in .NET karşılığı).
// ---------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddNpgSql(postgresConnStr, name: "postgres")
    .AddRedis(redisConnStr, name: "redis")
    .AddRabbitMQ($"amqp://guest:guest@{rabbitMqHost}:5672", name: "rabbitmq");

// ---------------------------------------------------------------------
// OpenTelemetry: Proje 1'deki Jaeger entegrasyonunun .NET karşılığı.
// MassTransit, kendi ActivitySource'unu ("MassTransit") zaten yayınlar;
// burada sadece onu dinlemesini söylüyoruz -- ekstra bir NuGet paketi ya
// da manuel span oluşturma gerekmiyor.
// ---------------------------------------------------------------------
var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://jaeger:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("order-service"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("MassTransit")
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

var app = builder.Build();

// Health check endpoint'i (bkz. docker-compose.yml HEALTHCHECK direktifi).
app.MapHealthChecks("/health");

// ---------------------------------------------------------------------
// POST /orders — Saga'yı başlatan API. Idempotency kontrolü burada değil,
// Saga seviyesinde (OrderSagaState.IdempotencyKey UNIQUE index) yapılıyor:
// aynı key ile SubmitOrder iki kez publish edilse bile MassTransit
// Correlation mekanizması ikinci mesajı AYNI (henüz var olan) saga
// instance'ına yönlendirir; yeni bir sipariş oluşmaz.
// ---------------------------------------------------------------------
app.MapPost("/orders", async (
    SubmitOrderRequest request,
    IPublishEndpoint publishEndpoint,
    OrderDbContext db) =>
{
    if (request.Items is null || request.Items.Count == 0)
        return Results.BadRequest(new { error = "En az bir sipariş kalemi gerekli" });
    if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        return Results.BadRequest(new { error = "idempotencyKey zorunludur" });

    // --- Kalıcı idempotency kontrolü ---
    // OrderSagaState'teki UNIQUE index sadece saga HENÜZ aktifken korur
    // (bkz. OrderSagaStateMap); saga tamamlanıp SetCompletedWhenFinalized()
    // ile silindiğinde bu koruma ortadan kalkar. Bu yüzden önce KALICI
    // deftere (OrderIdempotencyRecord) bakıyoruz -- daha önce tamamlanmış
    // bir sipariş varsa, yeni bir Saga/SubmitOrder hiç başlatmadan
    // doğrudan onu döneriz.
    var existing = await db.IdempotencyRecords
        .AsNoTracking()
        .SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey);
    if (existing is not null)
    {
        return Results.Ok(new { orderId = existing.OrderId, status = existing.FinalStatus });
    }

    var orderId = Guid.NewGuid();

    // Bu Publish çağrısı, Outbox aktif olduğu için doğrudan RabbitMQ'ya
    // gitmez -- OrderDbContext'in change tracker'ına buffer'lanır. Mesajın
    // GERÇEKTEN OutboxMessage tablosuna yazılması için SaveChangesAsync()
    // çağrılması ZORUNLUDUR -- bu çağrı olmadan Publish() sessizce hiçbir
    // şey yapmamış gibi davranır (hata fırlatmaz ama mesaj hiçbir yere
    // gitmez). Arka plandaki "bus outbox delivery service" bu tablodan
    // periyodik olarak okuyup RabbitMQ'ya güvenilir şekilde publish eder.
    await publishEndpoint.Publish(new SubmitOrder(
        orderId,
        request.CustomerId,
        request.Items.Select(i => new OrderLineItem(i.ProductId, i.Quantity, i.UnitPriceCents)).ToList(),
        request.IdempotencyKey));

    await db.SaveChangesAsync();

    return Results.Accepted($"/orders/{orderId}", new { orderId });
});

// ---------------------------------------------------------------------
// GET /orders/{id} — CQRS okuma tarafı: Postgres/Saga'ya DEĞİL, Redis'teki
// denormalize read model'e gider.
// ---------------------------------------------------------------------
app.MapGet("/orders/{id:guid}", async (Guid id, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var json = await db.StringGetAsync($"order-read-model:{id}");
    if (json.IsNullOrEmpty)
        return Results.NotFound();

    return Results.Content(json!, "application/json");
});

app.Run();

/// <summary>POST /orders isteğinin HTTP DTO'su (Contracts'taki command'dan
/// bilinçli olarak ayrı tutuluyor -- API sözleşmesi ile iç mesajlaşma
/// sözleşmesi birbirinden bağımsız evrilebilmeli).</summary>
public record SubmitOrderRequest(Guid CustomerId, List<SubmitOrderItem> Items, string IdempotencyKey);
public record SubmitOrderItem(string ProductId, int Quantity, long UnitPriceCents);