using System.Security.Cryptography;
using ClaimFlow.Bench;
using ClaimFlow.Security.Auth;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

if (!BenchOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 1;
}

Console.WriteLine($"Repository : {options.RepositoryRoot}");
Console.WriteLine($"Database   : {options.RedactedConnectionString()}");
Console.WriteLine($"RabbitMQ   : {options.RabbitHost}:{options.RabbitPort} as {options.RabbitUser}");
Console.WriteLine($"Intake     : {options.IntakeUrl}");
Console.WriteLine($"Adjudication: {options.AdjudicationUrl}");

var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
var jwt = new JwtOptions
{
    Issuer = "claimflow-bench",
    Audience = "claimflow-bench-api",
    SigningKey = signingKey,
    LifetimeMinutes = 120,
};

var database = new DatabaseAdmin(options);
ServiceHostRunner? hosts = null;
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(45));

try
{
    Console.WriteLine("Resetting and preparing the database...");
    await database.ResetAndPrepareAsync(cancellation.Token);
    PurgeQueues(options);

    hosts = new ServiceHostRunner(options, signingKey);
    await hosts.StartAsync(cancellation.Token);

    var runner = new BenchmarkRunner(options, database, jwt);
    var results = await runner.RunAsync(cancellation.Token);
    await ResultsWriter.WriteAsync(results, options, cancellation.Token);
    ResultsWriter.PrintSummary(results);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Benchmark failed: {ex}");
    if (hosts is not null)
    {
        Console.Error.WriteLine("--- service log tail ---");
        Console.Error.WriteLine(hosts.ReadLogTail());
    }

    return 1;
}
finally
{
    if (hosts is not null)
    {
        await hosts.DisposeAsync();
    }
}

static void PurgeQueues(BenchOptions options)
{
    var factory = new ConnectionFactory
    {
        HostName = options.RabbitHost,
        Port = options.RabbitPort,
        UserName = options.RabbitUser,
        Password = options.RabbitPassword,
        VirtualHost = "/",
        AutomaticRecoveryEnabled = false,
    };

    using var connection = factory.CreateConnection("claimflow-bench-reset");
    using var channel = connection.CreateModel();
    channel.ExchangeDeclare(ClaimFlow.Shared.Messaging.ClaimEventTypes.Exchange, ExchangeType.Topic, durable: true);
    Purge(channel, connection, "claimflow.adjudication", new Dictionary<string, object>
    {
        ["x-dead-letter-exchange"] = string.Empty,
        ["x-dead-letter-routing-key"] = "claimflow.dead-letter",
    });
    Purge(channel, connection, "claimflow.dead-letter", null);
    Console.WriteLine("Purged RabbitMQ queues.");
}

static void Purge(IModel channel, IConnection connection, string queue, Dictionary<string, object>? arguments)
{
    try
    {
        channel.QueueDeclarePassive(queue);
        channel.QueuePurge(queue);
        return;
    }
    catch (OperationInterruptedException)
    {
        // Queue missing or declared with different arguments; declare it explicitly.
    }

    try
    {
        using var fresh = connection.CreateModel();
        fresh.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false, arguments: arguments);
        fresh.QueuePurge(queue);
    }
    catch (OperationInterruptedException)
    {
        using var fresh = connection.CreateModel();
        fresh.QueuePurge(queue);
    }
}
