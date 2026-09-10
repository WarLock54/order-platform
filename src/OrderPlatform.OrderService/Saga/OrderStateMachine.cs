using MassTransit;
using OrderPlatform.Contracts;
using Serilog;

namespace OrderPlatform.OrderService.Saga;

public class OrderStateMachine : MassTransitStateMachine<OrderSagaState>
{
    public State Submitted { get; private set; } = null!;
    public State StockReservedState { get; private set; } = null!;
    public State PaymentProcessedState { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;

    public Event<SubmitOrder> SubmitOrderEvent { get; private set; } = null!;
    public Event<StockReserved> StockReservedEvent { get; private set; } = null!;
    public Event<StockReservationFailed> StockReservationFailedEvent { get; private set; } = null!;
    public Event<PaymentProcessed> PaymentProcessedEvent { get; private set; } = null!;
    public Event<PaymentFailed> PaymentFailedEvent { get; private set; } = null!;
    public Event<StockCommitted> StockCommittedEvent { get; private set; } = null!;
    public Event<StockReleased> StockReleasedEvent { get; private set; } = null!;

    public OrderStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => SubmitOrderEvent, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReservedEvent, x =>
        {
            x.CorrelateById(m => m.Message.OrderId);
            x.OnMissingInstance(m => m.ExecuteAsync(ctx =>
            {
                Log.Warning("[SAGA] StockReserved için EŞLEŞEN SAGA BULUNAMADI (muhtemel yarış durumu): OrderId={OrderId}", ctx.Message.OrderId);
                return Task.CompletedTask;
            }));
        });
        Event(() => StockReservationFailedEvent, x =>
        {
            x.CorrelateById(m => m.Message.OrderId);
            x.OnMissingInstance(m => m.ExecuteAsync(ctx =>
            {
                Log.Warning("[SAGA] StockReservationFailed için eşleşen saga bulunamadı: OrderId={OrderId}", ctx.Message.OrderId);
                return Task.CompletedTask;
            }));
        });
        Event(() => PaymentProcessedEvent, x =>
        {
            x.CorrelateById(m => m.Message.OrderId);
            x.OnMissingInstance(m => m.ExecuteAsync(ctx =>
            {
                Log.Warning("[SAGA] PaymentProcessed için eşleşen saga bulunamadı: OrderId={OrderId}", ctx.Message.OrderId);
                return Task.CompletedTask;
            }));
        });
        Event(() => PaymentFailedEvent, x =>
        {
            x.CorrelateById(m => m.Message.OrderId);
            x.OnMissingInstance(m => m.ExecuteAsync(ctx =>
            {
                Log.Warning("[SAGA] PaymentFailed için eşleşen saga bulunamadı: OrderId={OrderId}", ctx.Message.OrderId);
                return Task.CompletedTask;
            }));
        });
        Event(() => StockCommittedEvent, x =>
        {
            x.CorrelateById(m => m.Message.OrderId);
            x.OnMissingInstance(m => m.ExecuteAsync(ctx =>
            {
                Log.Warning("[SAGA] StockCommitted için eşleşen saga bulunamadı: OrderId={OrderId}", ctx.Message.OrderId);
                return Task.CompletedTask;
            }));
        });
        Event(() => StockReleasedEvent, x =>
        {
            x.CorrelateById(m => m.Message.OrderId);
            x.OnMissingInstance(m => m.ExecuteAsync(ctx =>
            {
                Log.Warning("[SAGA] StockReleased için eşleşen saga bulunamadı: OrderId={OrderId}", ctx.Message.OrderId);
                return Task.CompletedTask;
            }));
        });

        Initially(
            When(SubmitOrderEvent)
                .Then(ctx =>
                {
                    Log.Information("[SAGA] SubmitOrder alındı, yeni saga oluşturuluyor: OrderId={OrderId} IdempotencyKey={IdempotencyKey}",
                        ctx.Message.OrderId, ctx.Message.IdempotencyKey);
                    ctx.Saga.CustomerId = ctx.Message.CustomerId;
                    ctx.Saga.IdempotencyKey = ctx.Message.IdempotencyKey;
                    ctx.Saga.TotalCents = ctx.Message.Items.Sum(i => i.UnitPriceCents * i.Quantity);
                    ctx.Saga.CreatedAt = DateTime.UtcNow;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Publish(ctx => new ReserveStock(ctx.Message.OrderId, ctx.Message.Items, ctx.Message.IdempotencyKey))
                .TransitionTo(Submitted)
        );

        During(Submitted,
            When(StockReservedEvent)
                .Then(ctx =>
                {
                    Log.Information("[SAGA] StockReserved alındı, saga BULUNDU: OrderId={OrderId} CurrentState={CurrentState} ReservationId={ReservationId}",
                        ctx.Message.OrderId, ctx.Saga.CurrentState, ctx.Message.ReservationId);
                    ctx.Saga.ReservationId = ctx.Message.ReservationId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Publish(ctx => new ProcessPayment(ctx.Message.OrderId, ctx.Saga.CustomerId, ctx.Saga.TotalCents, "TRY", ctx.Saga.IdempotencyKey))
                .TransitionTo(StockReservedState),

            When(StockReservationFailedEvent)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = "insufficient_stock";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Publish(ctx => new OrderFailed(ctx.Message.OrderId, ctx.Saga.IdempotencyKey, "insufficient_stock"))
                .TransitionTo(Failed)
                .Finalize()
        );

        During(StockReservedState,
            When(PaymentProcessedEvent)
                .Then(ctx =>
                {
                    ctx.Saga.PaymentId = ctx.Message.PaymentId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Publish(ctx => new CommitStock(ctx.Message.OrderId, ctx.Saga.ReservationId!))
                .TransitionTo(PaymentProcessedState),

            When(PaymentFailedEvent)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = "payment_failed";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Publish(ctx => new ReleaseStock(ctx.Message.OrderId, ctx.Saga.ReservationId!, "payment_failed"))
                .TransitionTo(Failed)
        );

        During(Failed,
            When(StockReleasedEvent)
                .Publish(ctx => new OrderFailed(ctx.Saga.CorrelationId, ctx.Saga.IdempotencyKey, ctx.Saga.FailureReason ?? "unknown"))
                .Finalize()
        );

        During(PaymentProcessedState,
            When(StockCommittedEvent)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Publish(ctx => new OrderCompleted(ctx.Saga.CorrelationId, ctx.Saga.IdempotencyKey, ctx.Saga.PaymentId!, ctx.Saga.TotalCents))
                .TransitionTo(Completed)
                .Finalize()
        );

        SetCompletedWhenFinalized();
    }
}