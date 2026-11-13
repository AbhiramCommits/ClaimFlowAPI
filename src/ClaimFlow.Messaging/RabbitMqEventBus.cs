using System.Text;
using ClaimFlow.Shared.Messaging;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ClaimFlow.Messaging;

/// <summary>
/// RabbitMQ topic-exchange bus with durable queues, publisher confirms, exponential-backoff
/// consumer retries and an explicit dead-letter queue.
/// </summary>
public sealed class RabbitMqEventBus : IClaimEventBus
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IModel? _publishChannel;
    private bool _disposed;

    public RabbitMqEventBus(RabbitMqOptions options, ILogger<RabbitMqEventBus> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task PublishAsync(ClaimEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(ClaimEventJson.Serialize(envelope));
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetPublishChannelAsync(cancellationToken);
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.MessageId = envelope.EventId.ToString("D");
            properties.CorrelationId = envelope.CorrelationId;
            properties.Type = envelope.EventType;
            properties.Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds());
            properties.Headers = new Dictionary<string, object>
            {
                ["X-Correlation-Id"] = envelope.CorrelationId,
                ["X-Event-Type"] = envelope.EventType,
            };

            channel.BasicPublish(_options.Exchange, envelope.EventType, mandatory: false, properties, body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));

            _logger.LogInformation(
                "Published to RabbitMQ. EventType={EventType} EventId={EventId} CorrelationId={CorrelationId}",
                envelope.EventType,
                envelope.EventId,
                envelope.CorrelationId);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async Task<IEventSubscription> SubscribeAsync(
        string eventType,
        Func<ClaimEventEnvelope, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = CreateConsumerChannel(eventType);
        var consumer = new AsyncEventingBasicConsumer(channel);
        var closed = new CancellationTokenSource();

        consumer.Received += async (_, args) =>
        {
            ClaimEventEnvelope? envelope = null;
            try
            {
                var json = Encoding.UTF8.GetString(args.Body.Span);
                envelope = ClaimEventJson.Deserialize<ClaimEventEnvelope>(json);
                if (envelope is null)
                {
                    throw new InvalidOperationException("Event envelope deserialized to null.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poison message moved to dead-letter queue. DeliveryTag={DeliveryTag}", args.DeliveryTag);
                PublishToDeadLetter(channel, args, ex.Message, 0);
                channel.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
            {
                try
                {
                    await handler(envelope, closed.Token);
                    channel.BasicAck(args.DeliveryTag, multiple: false);
                    return;
                }
                catch (OperationCanceledException) when (closed.IsCancellationRequested)
                {
                    channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == _options.MaxRetries)
                    {
                        _logger.LogError(
                            ex,
                            "Event exhausted retries, moving to DLQ. EventId={EventId} EventType={EventType}",
                            envelope.EventId,
                            envelope.EventType);
                        PublishToDeadLetter(channel, args, ex.Message, attempt);
                        channel.BasicAck(args.DeliveryTag, multiple: false);
                        return;
                    }

                    var delay = TimeSpan.FromMilliseconds(
                        _options.RetryBaseDelayMilliseconds * Math.Pow(2, attempt));
                    _logger.LogWarning(
                        ex,
                        "Event handler failed, retrying in {DelayMs}ms. EventId={EventId} Attempt={Attempt}",
                        delay.TotalMilliseconds,
                        envelope.EventId,
                        attempt + 1);
                    await Task.Delay(delay, closed.Token);
                }
            }
        };

        var consumerTag = channel.BasicConsume(_options.QueueName, autoAck: false, consumer);
        _logger.LogInformation(
            "RabbitMQ consumer started. Queue={Queue} EventType={EventType} ConsumerTag={ConsumerTag}",
            _options.QueueName,
            eventType,
            consumerTag);

        await Task.CompletedTask;
        return new RabbitSubscription(channel, consumerTag, closed);
    }

    private IModel CreateConsumerChannel(string eventType)
    {
        EnsureConnected();
        var channel = _connection!.CreateModel();
        DeclareTopology(channel);
        channel.QueueBind(_options.QueueName, _options.Exchange, eventType);
        channel.BasicQos(0, prefetchCount: 8, global: false);
        return channel;
    }

    private void DeclareTopology(IModel channel)
    {
        channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, arguments: null);

        var queueArguments = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = _options.DeadLetterQueueName,
        };
        channel.QueueDeclare(_options.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: queueArguments);
        channel.QueueDeclare(_options.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
        channel.QueueBind(_options.DeadLetterQueueName, _options.Exchange, ClaimEventTypes.DeadLetter);
    }

    private async Task<IModel> GetPublishChannelAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_publishChannel is { IsOpen: true })
            {
                return _publishChannel;
            }

            EnsureConnected();
            _publishChannel = _connection!.CreateModel();
            DeclareTopology(_publishChannel);
            _publishChannel.ConfirmSelect();
            return _publishChannel;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is { IsOpen: true })
        {
            return;
        }

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };

        _connection = factory.CreateConnection("claimflow");
        _logger.LogInformation(
            "RabbitMQ connection established. Host={Host} VirtualHost={VirtualHost}",
            _options.HostName,
            _options.VirtualHost);
    }

    private void PublishToDeadLetter(IModel channel, BasicDeliverEventArgs args, string error, int retryCount)
    {
        try
        {
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.MessageId = args.BasicProperties?.MessageId;
            properties.CorrelationId = args.BasicProperties?.CorrelationId;
            properties.Headers = new Dictionary<string, object>
            {
                ["X-Original-Event-Type"] = args.RoutingKey,
                ["X-Retry-Count"] = retryCount,
                ["X-Error"] = error.Length > 512 ? error[..512] : error,
            };
            channel.BasicPublish(_options.Exchange, ClaimEventTypes.DeadLetter, properties, args.Body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish poison message to the dead-letter queue.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionLock.WaitAsync();
        try
        {
            _publishChannel?.Dispose();
            _connection?.Dispose();
        }
        finally
        {
            _connectionLock.Release();
            _connectionLock.Dispose();
            _publishLock.Dispose();
        }
    }

    private sealed class RabbitSubscription : IEventSubscription
    {
        private readonly IModel _channel;
        private readonly string _consumerTag;
        private readonly CancellationTokenSource _closed;

        public RabbitSubscription(IModel channel, string consumerTag, CancellationTokenSource closed)
        {
            _channel = channel;
            _consumerTag = consumerTag;
            _closed = closed;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (_channel.IsOpen)
                {
                    _channel.BasicCancel(_consumerTag);
                }
            }
            catch (Exception)
            {
                // The broker may already have closed the channel.
            }

            _closed.Cancel();
            _channel.Close();
            _channel.Dispose();
            _closed.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
