namespace WalletService.Domain;

public sealed class WalletCredentials
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProcessedCommand
{
    public string Id { get; set; } = string.Empty;
    public Guid WalletId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public Guid TransactionId { get; set; }
    public decimal Balance { get; set; }
    public long LedgerVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
