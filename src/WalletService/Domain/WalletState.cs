using Axiom.Ledger.Contracts;

namespace WalletService.Domain;

public sealed class WalletState
{
    public Guid Id { get; private set; }
    public decimal Balance { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static WalletState Create(WalletCreated @event)
    {
        return new WalletState
        {
            Id = @event.WalletId,
            Balance = 0m,
            Currency = @event.Currency,
            CreatedAt = @event.OccurredAt
        };
    }

    // Keeps existing pre-fix event streams readable.
    public static WalletState Create(WithdrawalRequested @event)
    {
        return new WalletState
        {
            Id = @event.WalletId,
            Balance = -@event.Amount,
            Currency = @event.Currency,
            CreatedAt = @event.OccurredAt
        };
    }

    public void Apply(MoneyDeposited @event)
    {
        Balance += @event.Amount;
    }

    public void Apply(MoneyWithdrawn @event)
    {
        Balance -= @event.Amount;
    }

    // Legacy event support for existing streams.
    public void Apply(WithdrawalRequested @event)
    {
        Balance -= @event.Amount;
    }
}
