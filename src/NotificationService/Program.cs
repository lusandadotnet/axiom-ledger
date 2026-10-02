using Axiom.Ledger.Contracts;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LedgerNotificationConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "axiom");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "axiom");
        });

        cfg.ReceiveEndpoint("ledger-notifications", endpoint =>
        {
            endpoint.UseMessageRetry(retry =>
                retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2)));
            endpoint.ConfigureConsumer<LedgerNotificationConsumer>(context);
        });
    });
});

await builder.Build().RunAsync();

public sealed class LedgerNotificationConsumer(ILogger<LedgerNotificationConsumer> logger)
    : IConsumer<LedgerTransactionOccurred>
{
    public Task Consume(ConsumeContext<LedgerTransactionOccurred> context)
    {
        logger.LogInformation(
            "Notification Sent — wallet {WalletId}, {TransactionType} {Amount} {Currency}, transaction {TransactionId}, ledger version {LedgerVersion}",
            context.Message.WalletId,
            context.Message.TransactionType,
            context.Message.Amount,
            context.Message.Currency,
            context.Message.TransactionId,
            context.Message.LedgerVersion);

        return Task.CompletedTask;
    }
}
