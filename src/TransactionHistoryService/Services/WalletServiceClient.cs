using System.Net;
using System.Net.Http.Json;
using Axiom.Ledger.Contracts;

namespace TransactionHistoryService.Services;

public sealed class WalletServiceClient(HttpClient client)
{
    public async Task<WalletAuthorizationInfo?> AuthorizeAsync(
        Guid walletId,
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/internal/wallets/{walletId}/authorize");

        request.Headers.TryAddWithoutValidation("X-Wallet-Token", token);

        using var response = await client.SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WalletAuthorizationInfo>(
            cancellationToken: cancellationToken);
    }

    public async Task<WalletProjectionData> GetProjectionDataAsync(
        Guid walletId,
        CancellationToken cancellationToken = default)
    {
        var result = await client.GetFromJsonAsync<WalletProjectionData>(
            $"/internal/wallets/{walletId}/projection-data",
            cancellationToken);

        return result ?? throw new InvalidOperationException("Wallet projection data was empty.");
    }

    public async Task<IReadOnlyList<Guid>> GetWalletIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await client.GetFromJsonAsync<List<Guid>>(
            "/internal/wallets",
            cancellationToken);

        return result ?? [];
    }
}
