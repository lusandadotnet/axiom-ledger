# Axiom Ledger


## Architecture

- **WalletService** — .NET 8 Web API, Marten event sourcing on PostgreSQL, `POST /api/withdraw`, and a Marten-backed transactional outbox.
- **YarpGateway** — YARP reverse proxy with a fixed-window rate limiter on withdrawals.
- **NotificationService** — .NET 8 Worker + MassTransit/RabbitMQ consumer that logs `Notification Sent`.
- **TransactionHistoryService** — .NET 8 Web API + gRPC server. Redis stores the flattened wallet read model and recent transactions.
- **React dashboard** — triggers withdrawals and immediately refreshes history after the synchronous gRPC projection succeeds.
- **Docker Compose** — Postgres, RabbitMQ, Redis, all three backend services, YARP, and the React app.

## Ticket coverage

| Ticket | Implementation |
|---|---|
| 001 | `WalletService`, `POST /api/withdraw`, Marten streams in Postgres, no CRUD transactions table |
| 002 | `YarpGateway`, POST/GET routes, 10 requests per 10 seconds per client IP on POST |
| 003 | RabbitMQ, Marten outbox document, MassTransit consumer worker |
| 004 | `TransactionHistoryService`, Redis read model, gRPC sync before 200 response, `/api/history` |
| 005 | React dashboard for withdrawal + current balance + recent transactions |
| 006 | Single root `docker-compose.yml` with internal DNS between containers |

## Run everything

```bash
docker compose up --build
```

Then open:

- React dashboard: http://localhost:3000
- YARP gateway: http://localhost:5100
- RabbitMQ management: http://localhost:15672 (`guest` / `guest`)

The dashboard uses the gateway through its `/api/*` proxy. You can also call the API directly:

```bash
curl -X POST http://localhost:5100/api/withdraw \
  -H 'Content-Type: application/json' \
  -d '{"walletId":"11111111-1111-1111-1111-111111111111","amount":100,"currency":"ZAR"}'

curl 'http://localhost:5100/api/history?walletId=11111111-1111-1111-1111-111111111111'
```

## Model

There is deliberately no `Transactions` CRUD table. A withdrawal is appended to the wallet's Marten event stream and stored in PostgreSQL's Marten tables (`mt_events` / `mt_streams`). The transaction-history service is a read model backed by Redis, not the source of truth.

The wallet balance in this ticket is derived as the negative sum of withdrawal events because no funding/deposit ticket exists yet. A future deposit/opening-balance event can extend the same event stream without introducing a CRUD transactions table.

 
