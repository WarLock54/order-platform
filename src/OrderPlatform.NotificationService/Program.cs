using MassTransit;
using Microsoft.Extensions.Hosting;
using OrderPlatform.NotificationService.Consumers;
using Serilog;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("service", "notification-service")
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();
builder.Services.AddSerilog();

var rabbitMqHost = builder.Configuration["RabbitMq:Host"] ?? "rabbitmq";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderCompletedConsumer>();
    x.AddConsumer<OrderFailedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();
await app.RunAsync();