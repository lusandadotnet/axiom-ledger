namespace Axiom.Ledger.Contracts;

public sealed record WithdrawalRequested(
    Guid TransactionId,
    Guid WalletId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt);
