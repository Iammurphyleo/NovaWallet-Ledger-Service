using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaWallet.Domain.Interfaces;

namespace NovaWallet.Infrastructure.BackgroundServices;

public sealed class OutboxProcessorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxProcessorService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public OutboxProcessorService(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessBatchAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken).ContinueWith(_ => { }); // swallow cancellation
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

        try
        {
            var messages = await repo.GetPendingAsync(batchSize: 50, ct);
            foreach (var msg in messages)
            {
                // In production this would publish to a message broker (e.g. Kafka / RabbitMQ).
                // For this exercise we emit a structured log entry as the "publication".
                _logger.LogInformation(
                    "Publishing outbox event {EventType} {MessageId}: {Payload}",
                    msg.Type, msg.Id, msg.Payload);

                await repo.MarkPublishedAsync(msg.Id, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Outbox processor encountered an error.");
        }
    }
}
