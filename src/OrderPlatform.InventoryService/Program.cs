using MassTransit;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderPlatform.InventoryService.Consumers;
using OrderPlatform.InventoryService.Infrastructure;
using Serilog;

// Bu bir "worker" servis (dışarıya iş API'si sunmuyor) ama Docker
// HEALTHCHECK'in sorgulayabileceği bir /health endpoint'ine ihtiyacı var;
// bu yüzden WebApplication kullanıyoruz, sadece o tek endpoint için.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
{
    config
        .Enrich.WithProperty("service", "inventory-service")
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter());
});

var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres eksik");
var rabbitMqHost = builder.Configuration["RabbitMq:Host"] ?? "rabbitmq";

builder.Services.AddDbContext<InventoryDbContext>(opt => opt.UseNpgsql(postgresConnStr));

builder.Services.AddMassTransit(x =>
{
    // Bu servisin kendi publish ettiği event'ler (StockReserved,
    // StockCommitted, StockReleased) için Transactional Outbox.
    x.AddEntityFrameworkOutbox<InventoryDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.AddConsumer<ReserveStockConsumer>();
    x.AddConsumer<CommitStockConsumer>();
    x.AddConsumer<ReleaseStockConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        // NOT: Burada daha önce PrefetchCount=1 vardı (40001 hatasını
        // önlemek için). Artık gerekli değil -- gerçek kök neden
        // ReserveStockConsumer'daki gereğinden katı SERIALIZABLE izolasyon
        // seviyesiydi, o ReadCommitted'e düşürüldü (bkz. o dosyadaki
        // yorum). Varsayılan prefetch ile tam throughput korunuyor.

        // Fault queue / retry policy (bkz. OrderService Program.cs'teki
        // aynı yorum -- C# raporunun risk #6 bulgusuna karşılık).
        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 3,
            minInterval: TimeSpan.FromMilliseconds(200),
            maxInterval: TimeSpan.FromSeconds(5),
            intervalDelta: TimeSpan.FromMilliseconds(200)));

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHealthChecks()
    .AddNpgSql(postgresConnStr, name: "postgres");

var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://jaeger:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("inventory-service"))
    .WithTracing(t => t
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("MassTransit")
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();