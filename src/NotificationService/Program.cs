using Axiom.Ledger.Contracts;
using MassTransit;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);


builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<WithdrawalNotificationConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        cfg.ReceiveEndpoint("withdrawal-notifications", endpoint =>
        {
            endpoint.ConfigureConsumer<WithdrawalNotificationConsumer>(context);
        });
    });
});

await builder.Build().RunAsync();

public sealed class WithdrawalNotificationConsumer(ILogger<WithdrawalNotificationConsumer> logger)
    : IConsumer<WithdrawalRequested>
{
    public Task Consume(ConsumeContext<WithdrawalRequested> context)
    {
        logger.LogInformation(
            "Notification Sent — wallet {WalletId}, withdrawal {Amount} {Currency}, transaction {TransactionId}",
            context.Message.WalletId,
            context.Message.Amount,
            context.Message.Currency,
            context.Message.TransactionId);

        return Task.CompletedTask;
    }
}
