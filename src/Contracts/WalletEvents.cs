namespace Axiom.Ledger.Contracts;

public sealed record WalletCreated(
    Guid WalletId,
    string Currency,
    DateTimeOffset OccurredAt);

public sealed record MoneyDeposited(
    Guid TransactionId,
    Guid WalletId,
    decimal Amount,
    string Currency,
    decimal BalanceAfter,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record MoneyWithdrawn(
    Guid TransactionId,
    Guid WalletId,
    decimal Amount,
    string Currency,
    decimal BalanceAfter,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record LedgerTransactionOccurred(
    Guid TransactionId,
    Guid WalletId,
    decimal Amount,
    string Currency,
    string TransactionType,
    decimal BalanceAfter,
    long LedgerVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WalletAuthorizationInfo(
    Guid WalletId,
    string Currency);

public sealed record WalletProjectionData(
    Guid WalletId,
    string Currency,
    decimal CurrentBalance,
    long CurrentVersion,
    IReadOnlyList<LedgerTransactionOccurred> Transactions);
