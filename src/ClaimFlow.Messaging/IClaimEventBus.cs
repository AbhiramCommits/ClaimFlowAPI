using ClaimFlow.Shared.Messaging;

namespace ClaimFlow.Messaging;

/// <summary>
/// At-least-once event bus. Publishers supply a complete envelope; subscribers receive every
/// message published to the routing key, even if the subscription starts later (queue semantics).
/// </summary>
public interface IClaimEventBus : IAsyncDisposable
{
    Task PublishAsync(ClaimEventEnvelope envelope, CancellationToken cancellationToken = default);

    Task<IEventSubscription> SubscribeAsync(
        string eventType,
        Func<ClaimEventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);
}

public interface IEventSubscription : IAsyncDisposable
{
}

public sealed record RabbitMqOptions(
    string HostName = "localhost",
    int Port = 5672,
    string UserName = "guest",
    string Password = "guest",
    string VirtualHost = "/",
    string Exchange = ClaimEventTypes.Exchange,
    string QueueName = "claimflow.adjudication",
    string DeadLetterQueueName = "claimflow.dead-letter",
    int MaxRetries = 3,
    int RetryBaseDelayMilliseconds = 500);
