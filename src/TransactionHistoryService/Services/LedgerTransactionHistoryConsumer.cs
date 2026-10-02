using Axiom.Ledger.Contracts;
using MassTransit;

namespace TransactionHistoryService.Services;

public sealed class LedgerTransactionHistoryConsumer(
    HistoryRepository repository,
    ILogger<LedgerTransactionHistoryConsumer> logger) : IConsumer<LedgerTransactionOccurred>
{
    public async Task Consume(ConsumeContext<LedgerTransactionOccurred> context)
    {
        var message = context.Message;

        await repository.ApplyAsync(message);

        logger.LogInformation(
            "Projected transaction {TransactionId} version {LedgerVersion} for wallet {WalletId}",
            message.TransactionId,
            message.LedgerVersion,
            message.WalletId);
    }
}
