using System.Text.Json;
using Axiom.Ledger.Contracts;
using Marten;
using MassTransit;

namespace WalletService.Infrastructure;

public sealed class OutboxPublisherWorker(
    IDocumentStore store,
    IPublishEndpoint publishEndpoint,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox drain failed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();

        var messages = await session.Query<OutboxMessage>()
            .OrderBy(x => x.OccurredAt)
            .Take(50)
            .ToListAsync();

        foreach (var message in messages)
        {
            switch (message.Type)
            {
                case nameof(LedgerTransactionOccurred):
                    var payload = JsonSerializer.Deserialize<LedgerTransactionOccurred>(message.Payload)
                        ?? throw new InvalidOperationException($"Invalid outbox payload {message.Id}");

                    await publishEndpoint.Publish(payload, cancellationToken);
                    session.Delete<OutboxMessage>(message.Id);

                    logger.LogInformation(
                        "Published transaction {TransactionId} version {LedgerVersion}",
                        payload.TransactionId,
                        payload.LedgerVersion);
                    break;

                default:
                    logger.LogWarning(
                        "Dropping unknown outbox message type {Type} with id {MessageId}",
                        message.Type,
                        message.Id);
                    session.Delete<OutboxMessage>(message.Id);
                    break;
            }
        }

        if (messages.Count > 0)
            await session.SaveChangesAsync(cancellationToken);
    }
}
