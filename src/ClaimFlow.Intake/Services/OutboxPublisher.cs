using System.Text.Json;
using ClaimFlow.Data;
using ClaimFlow.Shared.Messaging;
using ClaimFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Intake.Services;

/// <summary>
/// Drains the transactional outbox to the event bus. Publish-then-mark means delivery is
/// at-least-once: a crash between the two steps republishes on the next drain.
/// </summary>
public sealed class OutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClaimEventBus _bus;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IClaimEventBus bus,
        IConfiguration configuration,
        ILogger<OutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Outbox:Enabled", true))
        {
            _logger.LogInformation("Outbox publisher disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(_configuration.GetValue("Outbox:PollIntervalMs", 250));
        _logger.LogInformation("Outbox publisher started with poll interval {IntervalMs}ms.", interval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox drain failed; will retry.");
            }

            if (published == 0)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimFlowDbContext>();
        var batchSize = _configuration.GetValue("Outbox:BatchSize", 50);
        var messages = await db.OutboxMessages
            .Where(message => message.ProcessedAt == null)
            .OrderBy(message => message.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var publishedCount = 0;
        foreach (var message in messages)
        {
            var payload = ClaimEventJson.Deserialize<ClaimSubmittedPayload>(message.Payload);
            if (payload is null)
            {
                message.Attempts++;
                message.LastError = "Outbox payload could not be deserialized.";
                _logger.LogError("Outbox payload {OutboxId} could not be deserialized.", message.Id);
                continue;
            }

            var envelope = new ClaimEventEnvelope(
                payload.EventId,
                message.EventType,
                message.CorrelationId,
                message.OccurredAt,
                JsonSerializer.SerializeToElement(payload, ClaimEventJson.Options));

            try
            {
                await _bus.PublishAsync(envelope, cancellationToken);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.Attempts++;
                publishedCount++;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                _logger.LogError(ex, "Failed to publish outbox message {OutboxId}; will retry.", message.Id);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return publishedCount;
    }
}
