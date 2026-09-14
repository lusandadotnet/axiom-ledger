using System.Text.Json;
using Axiom.Ledger.Contracts;
using Grpc.Core;
using Axiom.Ledger.Contracts.History;
using Grpc.Net.Client;
using Marten;
using Weasel.Core;
using MassTransit;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using WalletService.Infrastructure;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddMarten(options =>
{
    options.Connection(builder.Configuration.GetConnectionString("Postgres")!);
    options.AutoCreateSchemaObjects = (dynamic)0; // scrappy but bypass enum, amap all to 0 i think review later


});

builder.Services.AddSingleton(sp =>
{
    var address = builder.Configuration["History:GrpcUrl"] ?? "http://transaction-history-service:8081";
    return GrpcChannel.ForAddress(address);
});
builder.Services.AddSingleton<HistorySync.HistorySyncClient>();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });
    });
});

builder.Services.AddHostedService<OutboxPublisherWorker>();

builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");

app.MapPost("/api/withdraw", async (WithdrawalRequest request, IDocumentStore store, HistorySync.HistorySyncClient historyClient, CancellationToken cancellationToken) =>
{
    if (request.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be greater than zero." });

    if (request.WalletId == Guid.Empty)
        return Results.BadRequest(new { error = "walletId is required." });

    if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length > 3)
        return Results.BadRequest(new { error = "currency must be a valid three-letter code." });

    await using var session = store.LightweightSession();

    var existing = await session.Events.FetchStreamAsync(request.WalletId);
    var currentBalance = existing
        .OfType<WithdrawalRequested>()
        .Sum(x => -x.Amount);

    var newBalance = currentBalance - request.Amount;
    var transactionId = Guid.NewGuid();
    var occurredAt = DateTimeOffset.UtcNow;

    var evt = new WithdrawalRequested(
        transactionId,
        request.WalletId,
        request.Amount,
        request.Currency.ToUpperInvariant(),
        occurredAt);

    session.Events.Append(request.WalletId, evt);

    session.Store(new OutboxMessage(
        transactionId,
        nameof(WithdrawalRequested),
        JsonSerializer.Serialize(evt),
        occurredAt));

    await session.SaveChangesAsync(cancellationToken);

    try
    {
        var reply = await historyClient.ApplyBalanceUpdateAsync(new BalanceUpdateRequest
        {
            WalletId = request.WalletId.ToString(),
            CurrentBalance = (double)newBalance,
            Currency = evt.Currency,
            TransactionId = transactionId.ToString(),
            Amount = (double)evt.Amount,
            TransactionType = "Withdrawal",
            OccurredAt = occurredAt.ToString("O")
        }, deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: cancellationToken);

        if (!reply.Accepted)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is RpcException || ex is IOException)
    {
        app.Logger.LogError(ex, "History sync failed for wallet {WalletId}", request.WalletId);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        transactionId,
        walletId = request.WalletId,
        amount = evt.Amount,
        currency = evt.Currency,
        balance = newBalance,
        occurredAt
    });
});

app.Run();

public sealed record WithdrawalRequest(Guid WalletId, decimal Amount, string Currency);
