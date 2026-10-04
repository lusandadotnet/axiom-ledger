using Xunit;
using Axiom.Ledger.Contracts;
using WalletService.Domain;

namespace Axiom.Ledger.Tests;

public sealed class WalletLedgerRulesTests
{
    [Fact]
    public void New_wallet_starts_with_zero_balance()
    {
        var walletId = Guid.NewGuid();

        var state = WalletState.Create(
            new WalletCreated(walletId, "ZAR", DateTimeOffset.UtcNow));

        Assert.Equal(0m, state.Balance);
        Assert.Equal("ZAR", state.Currency);
    }

    [Fact]
    public void Deposit_then_withdraw_never_creates_a_negative_balance()
    {
        var state = WalletState.Create(
            new WalletCreated(Guid.NewGuid(), "ZAR", DateTimeOffset.UtcNow));

        state.Apply(new MoneyDeposited(
            Guid.NewGuid(),
            state.Id,
            100m,
            "ZAR",
            100m,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow));

        var balanceAfter = WalletLedgerRules.CalculateBalanceAfter(
            state,
            "Withdrawal",
            40m,
            "ZAR");

        Assert.Equal(60m, balanceAfter);
    }

    [Fact]
    public void Withdrawal_larger_than_balance_is_rejected()
    {
        var state = WalletState.Create(
            new WalletCreated(Guid.NewGuid(), "ZAR", DateTimeOffset.UtcNow));

        var ex = Assert.Throws<LedgerRuleViolationException>(() =>
            WalletLedgerRules.CalculateBalanceAfter(
                state,
                "Withdrawal",
                100m,
                "ZAR"));

        Assert.Contains("Insufficient funds", ex.Message);
    }

    [Theory]
    [InlineData("ZA")]
    [InlineData("ZARX")]
    [InlineData("123")]
    [InlineData("@@@")]
    public void Currency_must_be_three_letters(string currency)
    {
        Assert.Throws<LedgerRuleViolationException>(() =>
            WalletLedgerRules.NormalizeCurrency(currency));
    }

    [Fact]
    public void Wallet_currency_is_fixed()
    {
        var state = WalletState.Create(
            new WalletCreated(Guid.NewGuid(), "ZAR", DateTimeOffset.UtcNow));

        var ex = Assert.Throws<LedgerRuleViolationException>(() =>
            WalletLedgerRules.CalculateBalanceAfter(
                state,
                "Deposit",
                100m,
                "USD"));

        Assert.Contains("Wallet currency is ZAR", ex.Message);
    }
}
