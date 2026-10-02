using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Axiom.Ledger.Contracts;
using Marten;
using Marten.Exceptions;
using MassTransit;
using WalletService.Domain;
using WalletService.Infrastructure;
using Weasel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services
    .AddMarten(options =>
    {
        options.Connection(builder.Configuration.GetConnectionString("Postgres")!);
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Events.AppendMode = EventAppendMode.Rich;
        options.Events.UseIdentityMapForAggregates = true;
        options.Projections.Snapshot<WalletState>(SnapshotLifecycle.Inline);
    })
    .UseLightweightSessions();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "axiom");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "axiom");
        });
    });
});

builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapPost("/api/wallets", async (
    CreateWalletRequest request,
    HttpRequest httpRequest,
    IDocumentStore store,
    CancellationToken cancellationToken) =>
{
    string currency;
    try
    {
        currency = WalletLedgerRules.NormalizeCurrency(request.Currency);
    }
    catch (LedgerRuleViolationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var walletId = Guid.NewGuid();
    var accessToken = GenerateAccessToken();
    var occurredAt = DateTimeOffset.UtcNow;

    await using var session = store.LightweightSession();

    session.Events.StartStream<WalletState>(
        walletId,
        new WalletCreated(walletId, currency, occurredAt));

    session.Store(new WalletCredentials
    {
        Id = walletId,
        TokenHash = HashToken(accessToken),
        Currency = currency,
        CreatedAt = occurredAt
    });

    try
    {
        await session.SaveChangesAsync(cancellationToken);
    }
    catch (ConcurrencyException)
    {
        return Results.Conflict(new { error = "Wallet creation conflicted with another request. Please try again." });
    }

    return Results.Created($"/api/wallets/{walletId}", new
    {
        walletId,
        accessToken,
        currency,
        balance = 0m,
        createdAt = occurredAt
    });
});

app.MapPost("/api/deposit", async (
    MoneyRequest request,
    HttpRequest httpRequest,
    IDocumentStore store,
    CancellationToken cancellationToken) =>
    await ProcessTransactionAsync("Deposit", request, httpRequest, store, cancellationToken));

app.MapPost("/api/withdraw", async (
    MoneyRequest request,
    HttpRequest httpRequest,
    IDocumentStore store,
    CancellationToken cancellationToken) =>
    await ProcessTransactionAsync("Withdrawal", request, httpRequest, store, cancellationToken));

app.MapGet("/internal/wallets/{walletId:guid}/authorize", async (
    Guid walletId,
    HttpRequest httpRequest,
    IDocumentStore store,
    CancellationToken cancellationToken) =>
{
    var token = httpRequest.Headers["X-Wallet-Token"].ToString();

    if (string.IsNullOrWhiteSpace(token))
        return Results.Unauthorized();

    await using var session = store.LightweightSession();
    var credentials = await session.LoadAsync<WalletCredentials>(walletId, cancellationToken);

    if (credentials is null || !TokenMatches(token, credentials.TokenHash))
        return Results.Unauthorized();

    return Results.Ok(new WalletAuthorizationInfo(walletId, credentials.Currency));
});

app.MapGet("/internal/wallets/{walletId:guid}/projection-data", async (
    Guid walletId,
    IDocumentStore store) =>
{
    await using var session = store.LightweightSession();
    var credentials = await session.LoadAsync<WalletCredentials>(walletId);

    if (credentials is null)
        return Results.NotFound(new { error = "Wallet not found." });

    var events = await session.Events.FetchStreamAsync(walletId);
    var transactions = new List<LedgerTransactionOccurred>();
    var currentBalance = 0m;

    foreach (var @event in events)
    {
        switch (@event.Data)
        {
            case WalletCreated:
                currentBalance = 0m;
                break;

            case MoneyDeposited deposited:
                currentBalance = deposited.BalanceAfter;
                transactions.Add(new LedgerTransactionOccurred(
                    deposited.TransactionId,
                    deposited.WalletId,
                    deposited.Amount,
                    deposited.Currency,
                    "Deposit",
                    deposited.BalanceAfter,
                    @event.Version,
                    deposited.IdempotencyKey,
                    deposited.OccurredAt));
                break;

            case MoneyWithdrawn withdrawn:
                currentBalance = withdrawn.BalanceAfter;
                transactions.Add(new LedgerTransactionOccurred(
                    withdrawn.TransactionId,
                    withdrawn.WalletId,
                    withdrawn.Amount,
                    withdrawn.Currency,
                    "Withdrawal",
                    withdrawn.BalanceAfter,
                    @event.Version,
                    withdrawn.IdempotencyKey,
                    withdrawn.OccurredAt));
                break;

            // Legacy events from the original implementation.
            case WithdrawalRequested legacy:
                currentBalance -= legacy.Amount;
                transactions.Add(new LedgerTransactionOccurred(
                    legacy.TransactionId,
                    legacy.WalletId,
                    legacy.Amount,
                    legacy.Currency,
                    "Withdrawal",
                    currentBalance,
                    @event.Version,
                    $"legacy:{legacy.TransactionId:N}",
                    legacy.OccurredAt));
                break;
        }
    }

    var currentVersion = events.Count == 0 ? 0L : events.Max(x => x.Version);

    return Results.Ok(new WalletProjectionData(
        walletId,
        credentials.Currency,
        currentBalance,
        currentVersion,
        transactions));
});

app.MapGet("/internal/wallets", async (IDocumentStore store) =>
{
    await using var session = store.QuerySession();
    var walletIds = await session.Query<WalletCredentials>()
        .Select(x => x.Id)
        .ToListAsync();

    return Results.Ok(walletIds);
});

app.Run();

static async Task<IResult> ProcessTransactionAsync(
    string operation,
    MoneyRequest request,
    HttpRequest httpRequest,
    IDocumentStore store,
    CancellationToken cancellationToken)
{
    if (request.WalletId == Guid.Empty)
        return Results.BadRequest(new { error = "walletId is required." });

    var token = httpRequest.Headers["X-Wallet-Token"].ToString();
    if (string.IsNullOrWhiteSpace(token))
        return Results.Unauthorized();

    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
    {
        return Results.BadRequest(new
        {
            error = "Idempotency-Key header is required and must be at most 100 characters."
        });
    }

    string currency;
    try
    {
        currency = WalletLedgerRules.NormalizeCurrency(request.Currency);
    }
    catch (LedgerRuleViolationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var commandId = BuildCommandId(request.WalletId, idempotencyKey);

    for (var attempt = 0; attempt < 2; attempt++)
    {
        await using var session = store.LightweightSession();

        var credentials = await session.LoadAsync<WalletCredentials>(
            request.WalletId,
            cancellationToken);

        if (credentials is null || !TokenMatches(token, credentials.TokenHash))
            return Results.Unauthorized();

        var processed = await session.LoadAsync<ProcessedCommand>(commandId, cancellationToken);

        if (processed is not null)
        {
            if (processed.Operation != operation
                || processed.Amount != request.Amount
                || processed.Currency != currency)
            {
                return Results.Conflict(new
                {
                    error = "Idempotency-Key has already been used for a different request."
                });
            }

            return Results.Ok(ToResponse(processed));
        }

        var stream = await session.Events.FetchForWriting<WalletState>(request.WalletId);

        if (stream.Aggregate is null)
            return Results.NotFound(new { error = "Wallet not found." });

        decimal balanceAfter;
        try
        {
            balanceAfter = WalletLedgerRules.CalculateBalanceAfter(
                stream.Aggregate,
                operation,
                request.Amount,
                currency);
        }
        catch (LedgerRuleViolationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }

        var transactionId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var ledgerVersion = stream.CurrentVersion + 1;

        object domainEvent = operation switch
        {
            "Deposit" => new MoneyDeposited(
                transactionId,
                request.WalletId,
                request.Amount,
                currency,
                balanceAfter,
                idempotencyKey,
                occurredAt),

            "Withdrawal" => new MoneyWithdrawn(
                transactionId,
                request.WalletId,
                request.Amount,
                currency,
                balanceAfter,
                idempotencyKey,
                occurredAt),

            _ => throw new InvalidOperationException($"Unsupported transaction operation '{operation}'.")
        };

        var publishedEvent = new LedgerTransactionOccurred(
            transactionId,
            request.WalletId,
            request.Amount,
            currency,
            operation,
            balanceAfter,
            ledgerVersion,
            idempotencyKey,
            occurredAt);

        stream.AppendOne(domainEvent);

        session.Store(new ProcessedCommand
        {
            Id = commandId,
            WalletId = request.WalletId,
            Operation = operation,
            Amount = request.Amount,
            Currency = currency,
            TransactionId = transactionId,
            Balance = balanceAfter,
            LedgerVersion = ledgerVersion,
            OccurredAt = occurredAt
        });

        session.Store(new OutboxMessage(
            transactionId,
            nameof(LedgerTransactionOccurred),
            JsonSerializer.Serialize(publishedEvent),
            occurredAt));

        try
        {
            await session.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                transactionId,
                walletId = request.WalletId,
                amount = request.Amount,
                currency,
                balance = balanceAfter,
                transactionType = operation,
                ledgerVersion,
                occurredAt
            });
        }
        catch (ConcurrencyException) when (attempt == 0)
        {
            // Re-read the wallet and retry once. A duplicate idempotency key will
            // return the already committed result on the next attempt.
        }
        catch (ConcurrencyException)
        {
            return Results.Conflict(new
            {
                error = "The wallet changed while this transaction was being processed. Retry the same Idempotency-Key."
            });
        }
    }

    return Results.Conflict(new
    {
        error = "The wallet changed while this transaction was being processed. Retry the same Idempotency-Key."
    });
}

static object ToResponse(ProcessedCommand command)
{
    return new
    {
        transactionId = command.TransactionId,
        walletId = command.WalletId,
        amount = command.Amount,
        currency = command.Currency,
        balance = command.Balance,
        transactionType = command.Operation,
        ledgerVersion = command.LedgerVersion,
        occurredAt = command.OccurredAt
    };
}

static string BuildCommandId(Guid walletId, string idempotencyKey)
{
    var digest = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));

    return $"{walletId:N}:{digest}";
}

static string GenerateAccessToken()
{
    return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

static string HashToken(string token)
{
    return Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

static bool TokenMatches(string token, string storedHash)
{
    try
    {
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var expectedHash = Convert.FromHexString(storedHash);
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
    catch (FormatException)
    {
        return false;
    }
}

public sealed record CreateWalletRequest(string Currency);
public sealed record MoneyRequest(Guid WalletId, decimal Amount, string Currency);
