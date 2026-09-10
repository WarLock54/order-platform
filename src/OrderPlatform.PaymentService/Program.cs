using MassTransit;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderPlatform.PaymentService.Consumers;
using OrderPlatform.PaymentService.Infrastructure;
using Polly;
using Polly.Extensions.Http;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
{
    config
        .Enrich.WithProperty("service", "payment-service")
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter());
});

var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres eksik");
var rabbitMqHost = builder.Configuration["RabbitMq:Host"] ?? "rabbitmq";

builder.Services.AddDbContext<PaymentDbContext>(opt => opt.UseNpgsql(postgresConnStr));

// ---------------------------------------------------------------------
// Polly: C# raporunda tutarsız kullanıldığı belirtilen (EOde(me) Gateway'de
// var, başka yerlerde yok) resilience kütüphanesi -- burada IHttpClientFactory
// ile birlikte, dış ödeme sağlayıcısı çağrıları için baştan standart
// olarak kuruluyor (bkz. PaymentGatewayClient). Proje 1'deki pkg/resilience
// (retry + gobreaker) ile birebir aynı amaç, .NET'in idiomatik yoluyla.
// ---------------------------------------------------------------------
builder.Services.AddHttpClient<PaymentGatewayClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PaymentGateway:BaseUrl"] ?? "https://example-payment-gateway.invalid");
    client.Timeout = TimeSpan.FromSeconds(5);
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError() // 5xx ve 408 hataları
    .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))))
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: 5, durationOfBreak: TimeSpan.FromSeconds(10)));

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.AddConsumer<ProcessPaymentConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

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
    .ConfigureResource(r => r.AddService("payment-service"))
    .WithTracing(t => t
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("MassTransit")
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
