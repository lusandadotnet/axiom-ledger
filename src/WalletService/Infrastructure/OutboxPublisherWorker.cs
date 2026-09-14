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

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
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
            if (message.Type == nameof(WithdrawalRequested))
            {
                var payload = JsonSerializer.Deserialize<WithdrawalRequested>(message.Payload)
                    ?? throw new InvalidOperationException($"Invalid outbox payload {message.Id}");

                await publishEndpoint.Publish(payload, cancellationToken);
                session.Delete<OutboxMessage>(message.Id);
                logger.LogInformation("Published withdrawal {TransactionId}", payload.TransactionId);
            }
        }

        if (messages.Count > 0)
            await session.SaveChangesAsync(cancellationToken);
    }
}
