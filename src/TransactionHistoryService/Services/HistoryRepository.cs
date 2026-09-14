using System.Text.Json;
using StackExchange.Redis;
using Axiom.Ledger.Contracts.History;

namespace TransactionHistoryService.Services;

public sealed class HistoryRepository(IConnectionMultiplexer redis)
{
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task ApplyAsync(
        BalanceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var walletKey = $"wallet:{request.WalletId}:read_model";
        var transactionsKey = $"wallet:{request.WalletId}:recent_transactions";

        var transaction = new RecentTransaction(
            Guid.Parse(request.TransactionId),
            request.WalletId,
            request.Amount,
            request.Currency,
            request.TransactionType,
            request.OccurredAt);

        var transactionJson = JsonSerializer.Serialize(transaction);

        var batch = _database.CreateBatch();

        var hash = new HashEntry[]
        {
            new("walletId", request.WalletId),
            new("balance", request.CurrentBalance),
            new("currency", request.Currency),
            new("updatedAt", request.OccurredAt)
        };

        batch.HashSetAsync(walletKey, hash);
        batch.ListLeftPushAsync(transactionsKey, transactionJson);
        batch.ListTrimAsync(transactionsKey, 0, 49);

        batch.Execute();
    }

    public async Task<HistoryResponse> GetHistoryAsync(
        Guid walletId,
        CancellationToken cancellationToken)
    {
        var walletKey = $"wallet:{walletId}:read_model";
        var transactionsKey = $"wallet:{walletId}:recent_transactions";

        var hashTask = _database.HashGetAllAsync(walletKey);
        var transactionsTask = _database.ListRangeAsync(transactionsKey, 0, 49);

        await Task.WhenAll(hashTask, transactionsTask);

        var hash = await hashTask;

        var transactions = (await transactionsTask)
            .Where(x => x.HasValue)
            .Select(x => JsonSerializer.Deserialize<RecentTransaction>(x!)!)
            .ToArray();

        var balance = 0d;
        var currency = "ZAR";

        var balanceValue = hash
            .FirstOrDefault(x => x.Name == "balance")
            .Value;

        if (balanceValue.HasValue)
        {
            double.TryParse(balanceValue!, out balance);
        }

        var currencyValue = hash
            .FirstOrDefault(x => x.Name == "currency")
            .Value;

        if (currencyValue.HasValue)
        {
            currency = currencyValue!;
        }

        return new HistoryResponse(
            walletId,
            balance,
            currency,
            transactions);
    }

    public sealed record HistoryResponse(
        Guid WalletId,
        double Balance,
        string Currency,
        IReadOnlyList<RecentTransaction> Transactions);

    public sealed record RecentTransaction(
        Guid TransactionId,
        string WalletId,
        double Amount,
        string Currency,
        string Type,
        string OccurredAt);
}