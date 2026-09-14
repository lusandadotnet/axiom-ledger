# Axiom Ledger

## Architecture

- **WalletService** — .NET 8 Web API, Marten event sourcing on PostgreSQL, `POST /api/withdraw`, and a Marten-backed transactional outbox.
- **YarpGateway** — YARP reverse proxy with a fixed-window rate limiter on withdrawals.
- **NotificationService** — .NET 8 Worker + MassTransit/RabbitMQ consumer that logs `Notification Sent`.
- **TransactionHistoryService** — .NET 8 Web API + gRPC server. Redis stores the flattened wallet read model and recent transactions.
- **React dashboard** — triggers withdrawals and immediately refreshes history after the synchronous gRPC projection succeeds.
- **Docker Compose** — Postgres, RabbitMQ, Redis, all three backend services, YARP, and the React app.

![Axiom Ledger Architecture Overview](./axion-ledger-overview.drawio.png)

[View the Axiom Ledger Entity Relationship Diagrams (PDF)](./axion-ledger-erds.pdf)

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
