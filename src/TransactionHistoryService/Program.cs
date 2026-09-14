using Grpc.AspNetCore.Server;
using StackExchange.Redis;
using TransactionHistoryService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis") ?? "redis:6379"));
builder.Services.AddSingleton<HistoryRepository>();

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});

var app = builder.Build();

app.MapGrpcService<HistorySyncService>();
app.MapHealthChecks("/health");

app.MapGet("/api/history", async (Guid? walletId, HistoryRepository repository, CancellationToken cancellationToken) =>
{
    if (walletId is null || walletId == Guid.Empty)
        return Results.BadRequest(new { error = "walletId query parameter is required." });

    var history = await repository.GetHistoryAsync(walletId.Value, cancellationToken);
    return Results.Ok(history);
});

app.Run();
