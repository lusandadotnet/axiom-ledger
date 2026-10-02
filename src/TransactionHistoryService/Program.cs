using MassTransit;
using StackExchange.Redis;
using TransactionHistoryService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LedgerTransactionHistoryConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "axiom");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "axiom");
        });

        cfg.ReceiveEndpoint("ledger-history", endpoint =>
        {
            endpoint.UseMessageRetry(retry =>
                retry.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)));
            endpoint.ConfigureConsumer<LedgerTransactionHistoryConsumer>(context);
        });
    });
});

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis") ?? "redis:6379"));
builder.Services.AddSingleton<HistoryRepository>();
builder.Services.AddHttpClient<WalletServiceClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["WalletService:BaseUrl"] ?? "http://wallet-service:8080");
});

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapGet("/api/history", async (
    Guid walletId,
    HttpRequest httpRequest,
    WalletServiceClient walletService,
    HistoryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (walletId == Guid.Empty)
        return Results.BadRequest(new { error = "walletId query parameter is required." });

    var token = httpRequest.Headers["X-Wallet-Token"].ToString();
    if (string.IsNullOrWhiteSpace(token))
        return Results.Unauthorized();

    try
    {
        var authorization = await walletService.AuthorizeAsync(
            walletId,
            token,
            cancellationToken);

        if (authorization is null)
            return Results.Unauthorized();

        var history = await repository.GetHistoryAsync(
            walletId,
            authorization.Currency,
            cancellationToken);

        return Results.Ok(history);
    }
    catch (HttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

// Local admin endpoint for restoring the Redis read model from the event store.
// The history service is only published on 127.0.0.1 in docker-compose.
app.MapPost("/internal/rebuild/{walletId:guid}", async (
    Guid walletId,
    WalletServiceClient walletService,
    HistoryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (walletId == Guid.Empty)
        return Results.BadRequest(new { error = "walletId is required." });

    try
    {
        var source = await walletService.GetProjectionDataAsync(
            walletId,
            cancellationToken);

        await repository.RebuildAsync(source);

        return Results.Ok(new
        {
            walletId,
            version = source.CurrentVersion,
            transactions = source.Transactions.Count,
            balance = source.CurrentBalance,
            currency = source.Currency
        });
    }
    catch (HttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/internal/rebuild-all", async (
    WalletServiceClient walletService,
    HistoryRepository repository,
    CancellationToken cancellationToken) =>
{
    try
    {
        var walletIds = await walletService.GetWalletIdsAsync(cancellationToken);
        var rebuilt = 0;

        foreach (var walletId in walletIds)
        {
            var source = await walletService.GetProjectionDataAsync(
                walletId,
                cancellationToken);

            await repository.RebuildAsync(source);
            rebuilt++;
        }

        return Results.Ok(new { wallets = rebuilt });
    }
    catch (HttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
