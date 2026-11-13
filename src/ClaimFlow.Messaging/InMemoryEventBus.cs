using System.Collections.Concurrent;
using System.Threading.Channels;
using ClaimFlow.Shared.Messaging;
using Microsoft.Extensions.Logging;

namespace ClaimFlow.Messaging;

/// <summary>
/// Process-local bus backed by unbounded channels. The hub is shared by name across all bus
/// instances in the process, which lets two WebApplicationFactory hosts talk to each other in
/// integration tests. Messages buffer until a subscriber drains them.
/// </summary>
public sealed class InMemoryEventBus : IClaimEventBus
{
    private static readonly ConcurrentDictionary<string, InMemoryHub> Hubs = new(StringComparer.Ordinal);

    private readonly InMemoryHub _hub;
    private readonly ILogger<InMemoryEventBus> _logger;

    public InMemoryEventBus(string hubName, ILogger<InMemoryEventBus> logger)
    {
        _hub = Hubs.GetOrAdd(hubName, _ => new InMemoryHub());
        _logger = logger;
    }

    public Task PublishAsync(ClaimEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "In-memory publish. EventType={EventType} EventId={EventId} CorrelationId={CorrelationId}",
            envelope.EventType,
            envelope.EventId,
            envelope.CorrelationId);
        _hub.Publish(envelope);
        return Task.CompletedTask;
    }

    public Task<IEventSubscription> SubscribeAsync(
        string eventType,
        Func<ClaimEventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = _hub.GetChannel(eventType);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loop = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in channel.Reader.ReadAllAsync(linked.Token))
                {
                    try
                    {
                        await handler(envelope, linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "In-memory subscriber failed for EventId={EventId}", envelope.EventId);
                    }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }, linked.Token);

        return Task.FromResult<IEventSubscription>(new Subscription(linked, loop));
    }

    public ValueTask DisposeAsync()
    {
        _hub.Complete();
        return ValueTask.CompletedTask;
    }

    private sealed class Subscription : IEventSubscription
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _loop;

        public Subscription(CancellationTokenSource cancellation, Task loop)
        {
            _cancellation = cancellation;
            _loop = loop;
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }

            _cancellation.Dispose();
        }
    }

    private sealed class InMemoryHub
    {
        private readonly ConcurrentDictionary<string, Channel<ClaimEventEnvelope>> _channels = new(StringComparer.Ordinal);

        public Channel<ClaimEventEnvelope> GetChannel(string eventType) =>
            _channels.GetOrAdd(eventType, _ => Channel.CreateUnbounded<ClaimEventEnvelope>(
                new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));

        public void Publish(ClaimEventEnvelope envelope)
        {
            GetChannel(envelope.EventType).Writer.TryWrite(envelope);
        }

        public void Complete()
        {
            foreach (var channel in _channels.Values)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
