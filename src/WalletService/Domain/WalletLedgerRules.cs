namespace WalletService.Domain;

public sealed class LedgerRuleViolationException(string message) : Exception(message);

public static class WalletLedgerRules
{
    public static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency)
            || currency.Length != 3
            || currency.Any(c => (c < 'A' || c > 'Z') && (c < 'a' || c > 'z')))
        {
            throw new LedgerRuleViolationException("currency must be exactly three letters.");
        }

        return currency.ToUpperInvariant();
    }

    public static decimal CalculateBalanceAfter(
        WalletState wallet,
        string operation,
        decimal amount,
        string currency)
    {
        if (amount <= 0m)
            throw new LedgerRuleViolationException("Amount must be greater than zero.");

        var normalizedCurrency = NormalizeCurrency(currency);

        if (!string.Equals(wallet.Currency, normalizedCurrency, StringComparison.Ordinal))
        {
            throw new LedgerRuleViolationException(
                $"Wallet currency is {wallet.Currency}; {normalizedCurrency} transactions are not allowed.");
        }

        return operation switch
        {
            "Deposit" => wallet.Balance + amount,
            "Withdrawal" when amount <= wallet.Balance => wallet.Balance - amount,
            "Withdrawal" => throw new LedgerRuleViolationException(
                $"Insufficient funds. Available balance is {wallet.Balance:0.00} {wallet.Currency}."),
            _ => throw new LedgerRuleViolationException($"Unsupported wallet operation '{operation}'.")
        };
    }
}
