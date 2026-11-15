using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TournamentPlatform.Messaging;

public static class RabbitMqTopology
{
    public const string Exchange = "tournament.events";
    public const string MatchCompletedRoutingKey = "match.completed";
    public const string TournamentsQueue = "tournaments.match-completed";
    public const string RatingQueue = "rating.match-completed";
}

public sealed record MatchCompletedEvent(
    Guid MatchId,
    Guid TournamentId,
    string TeamAId,
    string TeamBId,
    int ScoreA,
    int ScoreB,
    string WinnerId,
    DateTimeOffset CompletedAt);

public static class RabbitMqConnectionFactory
{
    public static ConnectionFactory Create(IConfiguration configuration, string connectionName) => new()
    {
        HostName = configuration["RabbitMQ:Host"] ?? "localhost",
        Port = int.TryParse(configuration["RabbitMQ:Port"], out var port) ? port : 5672,
        UserName = configuration["RabbitMQ:Username"] ?? "guest",
        Password = configuration["RabbitMQ:Password"] ?? "guest",
        ClientProvidedName = connectionName,
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
    };
}

public sealed class RabbitMqEventPublisher(IConfiguration configuration, ILogger<RabbitMqEventPublisher> logger)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishMatchCompletedAsync(MatchCompletedEvent message, CancellationToken cancellationToken)
    {
        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not { IsOpen: true } || _channel is not { IsOpen: true })
                await ConnectAsync(cancellationToken);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = message.MatchId.ToString()
            };
            await _channel!.BasicPublishAsync(
                RabbitMqTopology.Exchange,
                RabbitMqTopology.MatchCompletedRoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: JsonSerializer.SerializeToUtf8Bytes(message),
                cancellationToken: cancellationToken);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();

        _connection = await RabbitMqConnectionFactory.Create(configuration, "matches-publisher")
            .CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await _channel.ExchangeDeclareAsync(
            RabbitMqTopology.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        logger.LogInformation("Connected to RabbitMQ and declared event exchange {Exchange}.", RabbitMqTopology.Exchange);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _channelLock.Dispose();
    }
}

public abstract class MatchCompletedConsumer(
    IConfiguration configuration,
    ILogger logger,
    string queueName) : BackgroundService
{
    protected abstract Task HandleAsync(MatchCompletedEvent message, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RabbitMQ consumer {Queue} stopped. Retrying in 5 seconds.", queueName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await RabbitMqConnectionFactory.Create(configuration, $"{queueName}-consumer")
            .CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            RabbitMqTopology.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queueName, RabbitMqTopology.Exchange,
            RabbitMqTopology.MatchCompletedRoutingKey, cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, 1, false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize<MatchCompletedEvent>(eventArgs.Body.Span)
                    ?? throw new JsonException("Message body was empty.");
                await HandleAsync(message, cancellationToken);
                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken);
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Invalid MatchCompleted message in queue {Queue}; discarding it.", queueName);
                await channel.BasicRejectAsync(eventArgs.DeliveryTag, requeue: false, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process MatchCompleted message in queue {Queue}; requeuing it.", queueName);
                await channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true, cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken);
        logger.LogInformation("RabbitMQ consumer is listening on queue {Queue}.", queueName);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
