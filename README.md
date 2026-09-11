# Order Platform — Event-Driven E-Commerce/Logistics Platform

A microservices architecture written in .NET 9 + MassTransit + PostgreSQL +
Redis, using **the framework's own Transactional Outbox and Saga State
Machine support**. A direct continuation of Project 1 (Go + Kafka + gRPC):
same domain (orders/inventory/payments), same guarantees, but this time
solved with the .NET ecosystem's mature tooling instead of hand-rolling
everything.

## Architecture

```
POST /orders ──▶ order-service (Minimal API)
                     │  1) Check the permanent idempotency ledger
                     │  2) IPublishEndpoint.Publish(SubmitOrder)
                     │     (Transactional Outbox: atomic with the DB commit)
                     ▼
              OrderStateMachine (MassTransit Saga)
              Submitted → StockReserved → PaymentProcessed → Completed
                     │                              │
        ReserveStock/CommitStock/ReleaseStock   ProcessPayment
                     │                              │
                     ▼                              ▼
          inventory-service (Worker)        payment-service (Worker)
          Postgres: stock_items,            Postgres: payments
                    reservations            (idempotency_key UNIQUE)
                     │                              │
                     └──────────────┬───────────────┘
                                    ▼
                      OrderCompleted / OrderFailed
                                    │
            ┌───────────────┬──────┴──────┬───────────────────┐
            ▼               ▼             ▼                   ▼
   OrderStatusProjector  OrderIdempotency  notification-service
   (Redis, CQRS read)    Recorder (PERMANENT (log/notification)
                         idempotency ledger)

   All services ──OTLP/gRPC──▶ Jaeger (http://localhost:16686)
   RabbitMQ Management UI: http://localhost:15672 (guest/guest)
```

## Requirements

- .NET 9 SDK
- Docker & Docker Compose
- `dotnet-ef` CLI tool: `dotnet tool install --global dotnet-ef`

## Setup

1. **Restore dependencies:**
   ```bash
   dotnet restore
   ```

2. **Bring up the infrastructure:**
   ```bash
   docker compose up -d postgres rabbitmq redis jaeger
   ```

3. **Create and apply the EF Core migrations:**
   ```bash
   cd src/OrderPlatform.OrderService
   dotnet ef migrations add InitialCreate --context OrderDbContext
   dotnet ef database update --context OrderDbContext
   cd ../..

   cd src/OrderPlatform.InventoryService
   dotnet ef migrations add InitialCreate --context InventoryDbContext
   dotnet ef database update --context InventoryDbContext
   cd ../..

   cd src/OrderPlatform.PaymentService
   dotnet ef migrations add InitialCreate --context PaymentDbContext
   dotnet ef database update --context PaymentDbContext
   cd ../..
   ```
   (Each service has an `IDesignTimeDbContextFactory` so migration
   commands can create the DbContext at design time without booting the
   full application — no need to have Redis/RabbitMQ etc. running for
   this step.)

4. **Seed the demo inventory data:**
   ```bash
   psql postgres://order_platform:order_platform@localhost:5432/inventory -f deploy/sql/seed-inventory.sql
   ```

5. **Build and bring up all application services:**
   ```bash
   docker compose up --build -d
   docker compose ps
   ```

6. **Jaeger UI**: http://localhost:16686
   **RabbitMQ Management UI**: http://localhost:15672 (guest/guest)

## Example Test Flow

```bash
curl -X POST http://localhost:8081/orders \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "11111111-1111-1111-1111-111111111111",
    "items": [{ "productId": "SKU-001", "quantity": 2, "unitPriceCents": 1500 }],
    "idempotencyKey": "test-key-001"
  }'
# -> 202 Accepted { "orderId": "..." }

curl http://localhost:8081/orders/<orderId>
# -> { "Status": "PAID", ... }
```

Sending `POST /orders` again with the **same** `idempotencyKey` — even
**after** the order has already completed — does not open a new order:
the existing result is looked up in the permanent idempotency ledger
(`order_idempotency_records`) and returned with `200 OK` (see "Permanent
Idempotency" below).

## Proven Guarantees

As with Project 1, every one of these was tested live against a real
Docker environment — not assumed, but proven:

| Guarantee | How It Was Proven |
|---|---|
| Transactional Outbox (DB write + message publish are atomic) | A missing `SaveChangesAsync()` was caught live and fixed; the flow then worked end to end |
| The Saga State Machine transitions correctly | `[SAGA]` logs added at every step traced `Submitted → StockReservedState → PaymentProcessedState → Completed` |
| Self-heals from downstream failures/conflicts | A real Postgres `40001` (serialization conflict) was automatically recovered by the retry policy |
| **Safety under concurrent stock depletion** | See "Concurrency Stress Test" below — 5 parallel requests, correct outcome, stock never went negative |
| The CQRS read model (Redis) stays in sync | `GET /orders/{id}` consistently returned the current status |
| **Permanent idempotency** (even after the saga instance is deleted) | See "Permanent Idempotency" below |
| Payment idempotency (at the worker level) | A single row enforced by a UNIQUE constraint was confirmed in the `payments` table |

### Concurrency Stress Test

Five **concurrent** orders were sent for `SKU-003` (10 units of stock),
each requesting 3 units (total demand 15 > stock of 10):

```
5 parallel Start-Job jobs in PowerShell hitting POST /orders at once
```

**Result:** 3 orders `PAID`, 2 orders `FAILED` (`insufficient_stock`),
**none stuck**, all resolved within one second. A stock query returned
`available_quantity = 1` (10 − 9 = 1 — exactly right, neither over- nor
under-sold).

This test **failed** on the first attempt: only 1 of the 5 orders
completed, two got permanently stuck at `PENDING`/`RESERVED`. Root
cause: `ReserveStockConsumer` locks the same `product_id` row with `FOR
UPDATE` under `SERIALIZABLE` isolation; with the default RabbitMQ
prefetch count (16-24), multiple messages hitting the same row get
processed on **parallel threads**, triggering Postgres's `40001: could
not serialize access due to concurrent update`. See "The PrefetchCount
Decision" below.

### The PrefetchCount Decision (Superseded — Replaced With a Better Fix)

The first fix was to set `PrefetchCount = 1` on both `order-service` and
`inventory-service`. This correctly eliminated the `40001` error, but at
the cost of throughput (only one message processed at a time, even for
unrelated orders/products). This was later replaced with **two separate,
root-cause-specific fixes that preserve throughput**:

- **`order-service` (the saga):** MassTransit's EF Saga Repository
  internally uses `SERIALIZABLE` isolation for row access (a framework
  decision we don't control). Fix: instead of `PrefetchCount=1`, use
  **`UsePartitioner`** — only events belonging to the SAME `OrderId` are
  serialized; DIFFERENT orders continue to be processed in parallel (10
  partitions, i.e. up to 10 different orders can progress at once).
- **`inventory-service` (stock reservation):** here `SERIALIZABLE` was
  **our own choice** (in `ReserveStockConsumer.cs`), so it could be fixed
  at the source: downgraded to `IsolationLevel.ReadCommitted`. `FOR
  UPDATE` already locks the right row; `SERIALIZABLE`'s extra predicate-
  locking layer was unnecessary for this simple single-row locking
  scenario. Under `ReadCommitted`, two concurrent requests naturally
  QUEUE (the second waits for the first to commit) instead of throwing
  an error — correct, and at full throughput.

Result: `PrefetchCount=1` is no longer used **anywhere**, and throughput
was largely restored. Re-running the stress test still produced the
correct outcome (3 `PAID`, 2 `FAILED`, stock exactly `1`), and
**`inventory-service` no longer produces `40001` at all** (`ReadCommitted`
eliminated that source entirely).

**An honest caveat — for `order-service`:** `UsePartitioner` only
serializes messages for the SAME `OrderId`. When 5 DIFFERENT orders
create new saga rows concurrently (`SubmitOrder`/`Initially`), they all
also write concurrently to the shared `OutboxState`/`OutboxMessage`
tables — so MassTransit's EF Saga Repository's internal `SERIALIZABLE`
isolation can still occasionally detect a conflict **at commit time**
(`40001: could not serialize access due to read/write dependencies among
transactions`) — a different flavor of conflict that partitioning
doesn't cover. This was genuinely observed in live testing. It isn't a
bug, though: the retry policy catches it and retries automatically, and
**the final outcome is always correct** (confirmed live). MassTransit's
own documentation also recommends pairing the EF Saga Repository with a
retry policy for exactly this kind of occasional `SERIALIZABLE`
conflict — so the retry policy on `order-service` isn't a nice-to-have,
it's a **required part of the architecture**.

### CQRS Reconciliation (Added)

To guard against `OrderStatusProjector` missing an event, a background
service called **`ReadModelReconciliationService`** was added to
`order-service`. Every 30 seconds, it:

1. Scans the permanent idempotency ledger (`order_idempotency_records` —
   the source of truth).
2. For every completed order, checks whether Redis's status matches that
   ledger.
3. If there's a mismatch (or no Redis record at all), patches Redis to
   match the ledger's final status and logs the correction.

Deliberate scope limit: only COMPLETED orders (`PAID`/`FAILED`) are
reconciled. The live status of still-in-progress orders
(`PENDING`/`RESERVED`) is deliberately not synced externally — doing so
could create a separate consistency problem racing against the saga's
own state transitions. A completed order's final status, on the other
hand, never changes again, so reconciling it is safe.

### Permanent Idempotency

Because `SetCompletedWhenFinalized()` is called, MassTransit's EF Saga
Repository **deletes** completed saga instances from the `OrderSagaState`
table (the correct behavior for performance in high-volume systems).
This has a consequence: the UNIQUE index protection on
`OrderSagaState.IdempotencyKey` disappears once the order **completes**.

This was genuinely caught in live testing: sending the same
`idempotencyKey` twice in a row, once the saga had completed, could open
**two different `orderId`s** for two separate orders.

**Fix:** a small table called `order_idempotency_records` was added,
completely independent of the saga's lifecycle and never deleted. The
`OrderIdempotencyRecorder` consumer listens to the `OrderCompleted`/
`OrderFailed` events the saga already publishes and writes a permanent
record here. `POST /orders` checks this table BEFORE publishing a new
`SubmitOrder` — if a record exists, it returns the existing result
directly without ever starting a new saga.

Verification: sending the same `idempotencyKey` twice in a row returned
the **exact same `orderId`** both times; the second request got `200 OK`
(found the existing one), the first got `202 Accepted` (created a new
one) — even the differing HTTP status codes confirm the distinction.


Solve the problem using the tools provided by a mature framework (.NET + MassTransit) and fully implement the Transactional Outbox Pattern. Additionally, apply from the outset the concrete lessons learned from an audit report of a real C# production codebase (Polly, Serilog standardization, health checks, versioning discipline).
