using System.Globalization;
using System.Text.Json;
using Axiom.Ledger.Contracts;
using StackExchange.Redis;

namespace TransactionHistoryService.Services;

public sealed class HistoryRepository(IConnectionMultiplexer redis)
{
    private const string ApplyScript = """
        local currentVersion = redis.call('HGET', KEYS[1], 'version')
        local incomingVersion = tonumber(ARGV[1])

        if currentVersion and incomingVersion > tonumber(currentVersion) then
            redis.call('HSET', KEYS[1],
                'walletId', ARGV[2],
                'balance', ARGV[3],
                'currency', ARGV[4],
                'version', ARGV[1],
                'updatedAt', ARGV[5])
        elseif not currentVersion then
            redis.call('HSET', KEYS[1],
                'walletId', ARGV[2],
                'balance', ARGV[3],
                'currency', ARGV[4],
                'version', ARGV[1],
                'updatedAt', ARGV[5])
        end

        redis.call('HSET', KEYS[2], ARGV[6], ARGV[7])
        redis.call('ZADD', KEYS[3], incomingVersion, ARGV[6])

        local count = redis.call('ZCARD', KEYS[3])
        if count > 50 then
            local stale = redis.call('ZRANGE', KEYS[3], 0, count - 51)
            for _, transactionId in ipairs(stale) do
                redis.call('ZREM', KEYS[3], transactionId)
                redis.call('HDEL', KEYS[2], transactionId)
            end
        end

        return 1
        """;

    private readonly IDatabase database = redis.GetDatabase();

    public async Task ApplyAsync(LedgerTransactionOccurred message)
    {
        var walletKey = GetWalletKey(message.WalletId);
        var transactionDataKey = GetTransactionDataKey(message.WalletId);
        var transactionVersionKey = GetTransactionVersionKey(message.WalletId);

        var transaction = new RecentTransaction(
            message.TransactionId,
            message.WalletId,
            message.Amount,
            message.Currency,
            message.TransactionType,
            message.OccurredAt,
            message.LedgerVersion);

        var transactionJson = JsonSerializer.Serialize(transaction);

        await database.ScriptEvaluateAsync(
            ApplyScript,
            new RedisKey[] { walletKey, transactionDataKey, transactionVersionKey },
            new RedisValue[]
            {
                message.LedgerVersion,
                message.WalletId.ToString(),
                message.BalanceAfter.ToString(CultureInfo.InvariantCulture),
                message.Currency,
                message.OccurredAt.ToString("O"),
                message.TransactionId.ToString(),
                transactionJson
            });
    }

    public async Task<HistoryResponse> GetHistoryAsync(
        Guid walletId,
        string fallbackCurrency,
        CancellationToken cancellationToken = default)
    {
        var walletKey = GetWalletKey(walletId);
        var transactionDataKey = GetTransactionDataKey(walletId);
        var transactionVersionKey = GetTransactionVersionKey(walletId);

        var hashTask = database.HashGetAllAsync(walletKey);
        var versionedIdsTask = database.SortedSetRangeByRankAsync(
            transactionVersionKey,
            0,
            49,
            Order.Descending);

        await Task.WhenAll(hashTask, versionedIdsTask);

        var hash = await hashTask;
        var transactionIds = await versionedIdsTask;

        var transactionPayloads = transactionIds.Length == 0
            ? []
            : await database.HashGetAsync(transactionDataKey, transactionIds);

        var transactions = new List<RecentTransaction>(transactionPayloads.Length);

        foreach (var payload in transactionPayloads)
        {
            if (!payload.HasValue)
                continue;

            var transaction = JsonSerializer.Deserialize<RecentTransaction>(payload.ToString());
            if (transaction is not null)
                transactions.Add(transaction);
        }

        transactions.Sort((left, right) => right.LedgerVersion.CompareTo(left.LedgerVersion));

        var balance = GetDecimal(hash, "balance", 0m);
        var currency = GetString(hash, "currency", fallbackCurrency);
        var version = GetLong(hash, "version", 0L);

        return new HistoryResponse(
            walletId,
            balance,
            currency,
            version,
            transactions);
    }

    public async Task RebuildAsync(WalletProjectionData source)
    {
        var walletKey = GetWalletKey(source.WalletId);
        var transactionDataKey = GetTransactionDataKey(source.WalletId);
        var transactionVersionKey = GetTransactionVersionKey(source.WalletId);

        await database.KeyDeleteAsync(new RedisKey[]
        {
            walletKey,
            transactionDataKey,
            transactionVersionKey
        });

        foreach (var transaction in source.Transactions.OrderBy(x => x.LedgerVersion))
            await ApplyAsync(transaction);

        await database.HashSetAsync(walletKey, new HashEntry[]
        {
            new("walletId", source.WalletId.ToString()),
            new("balance", source.CurrentBalance.ToString(CultureInfo.InvariantCulture)),
            new("currency", source.Currency),
            new("version", source.CurrentVersion),
            new("updatedAt", DateTimeOffset.UtcNow.ToString("O"))
        });
    }

    private static string GetWalletKey(Guid walletId) => $"wallet:{walletId}:read_model";
    private static string GetTransactionDataKey(Guid walletId) => $"wallet:{walletId}:transactions";
    private static string GetTransactionVersionKey(Guid walletId) => $"wallet:{walletId}:transaction_versions";

    private static string GetString(IEnumerable<HashEntry> hash, string name, string fallback)
    {
        var value = hash.FirstOrDefault(x => x.Name == name).Value;
        return value.HasValue ? value.ToString() : fallback;
    }

    private static decimal GetDecimal(IEnumerable<HashEntry> hash, string name, decimal fallback)
    {
        var value = hash.FirstOrDefault(x => x.Name == name).Value;

        return value.HasValue
            && decimal.TryParse(
                value.ToString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var result)
            ? result
            : fallback;
    }

    private static long GetLong(IEnumerable<HashEntry> hash, string name, long fallback)
    {
        var value = hash.FirstOrDefault(x => x.Name == name).Value;

        return value.HasValue
            && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    public sealed record HistoryResponse(
        Guid WalletId,
        decimal Balance,
        string Currency,
        long Version,
        IReadOnlyList<RecentTransaction> Transactions);

    public sealed record RecentTransaction(
        Guid TransactionId,
        Guid WalletId,
        decimal Amount,
        string Currency,
        string Type,
        DateTimeOffset OccurredAt,
        long LedgerVersion);
}
