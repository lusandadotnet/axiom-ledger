using Axiom.Ledger.Contracts.History;
using Grpc.Core;

namespace TransactionHistoryService.Services;

public sealed class HistorySyncService(HistoryRepository repository) : HistorySync.HistorySyncBase
{
    public override async Task<BalanceUpdateResponse> ApplyBalanceUpdate(
        BalanceUpdateRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.WalletId, out _)
            || !Guid.TryParse(request.TransactionId, out _)
            || request.Amount <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid balance update."));
        }

        await repository.ApplyAsync(request);

        return new BalanceUpdateResponse
        {
            Accepted = true,
            WalletId = request.WalletId
        };
    }
}
