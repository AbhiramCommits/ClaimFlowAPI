using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaimFlow.Messaging;

public static class ClaimEventBusRegistration
{
    /// <summary>
    /// Registers the bus using configuration resolved at service-resolution time, so test and
    /// environment overrides are honoured.
    /// </summary>
    public static IServiceCollection AddClaimEventBus(
        this IServiceCollection services,
        string defaultHubName = "claimflow")
    {
        services.AddSingleton<IClaimEventBus>(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var provider = configuration["Messaging:Provider"] ?? "RabbitMq";

            if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
            {
                var hubName = configuration["Messaging:InMemoryHubName"] ?? defaultHubName;
                return new InMemoryEventBus(hubName, loggerFactory.CreateLogger<InMemoryEventBus>());
            }

            var options = new RabbitMqOptions(
                HostName: configuration["RabbitMq:HostName"] ?? "localhost",
                Port: configuration.GetValue("RabbitMq:Port", 5672),
                UserName: configuration["RabbitMq:UserName"] ?? "guest",
                Password: configuration["RabbitMq:Password"] ?? "guest",
                VirtualHost: configuration["RabbitMq:VirtualHost"] ?? "/",
                QueueName: configuration["RabbitMq:QueueName"] ?? "claimflow.adjudication",
                DeadLetterQueueName: configuration["RabbitMq:DeadLetterQueueName"] ?? "claimflow.dead-letter",
                MaxRetries: configuration.GetValue("RabbitMq:MaxRetries", 3),
                RetryBaseDelayMilliseconds: configuration.GetValue("RabbitMq:RetryBaseDelayMs", 500),
                ConsumerConcurrency: configuration.GetValue("RabbitMq:ConsumerConcurrency", 4));
            return new RabbitMqEventBus(options, loggerFactory.CreateLogger<RabbitMqEventBus>());
        });

        return services;
    }
}
