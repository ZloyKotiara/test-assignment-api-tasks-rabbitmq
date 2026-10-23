using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TaskApi.Messaging;

public sealed class RabbitMqTransport(
    IOptions<RabbitMqOptions> options, ILogger<RabbitMqTransport> logger) : IRabbitMqTransport
{
    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(TaskCompletedEvent message, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Persistent = true,
                Type = "task.completed",
                // Completion is irreversible, so this is a stable logical event ID.
                MessageId = $"task.completed:{message.TaskId:D}"
            };

            await _channel!.BasicPublishAsync(
                exchange: _options.Exchange,
                routingKey: _options.RoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: JsonSerializer.SerializeToUtf8Bytes(message, TaskJson.Options),
                cancellationToken: cancellationToken);
        }
        catch
        {
            // Recreate the connection on the next event; do not replay this event.
            // Abort/dispose is bounded, including when the publish token expired.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await CloseAsync(cleanup.Token);
            throw;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
            return;

        await CloseAsync(cancellationToken);
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(_options.PublishTimeoutSeconds),
            ContinuationTimeout = TimeSpan.FromSeconds(_options.PublishTimeoutSeconds),
            ClientProvidedName = "task-api-publisher"
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false),
            cancellationToken);

        _channel.BasicReturnAsync += (_, returned) =>
        {
            logger.LogWarning(
                "RabbitMQ returned an unroutable event. Reply={Reply}; Exchange={Exchange}; RoutingKey={RoutingKey}; Event={Event}",
                returned.ReplyText, returned.Exchange, returned.RoutingKey,
                Encoding.UTF8.GetString(returned.Body.Span));
            return Task.CompletedTask;
        };

        await _channel.ExchangeDeclareAsync(
            exchange: _options.Exchange, type: ExchangeType.Topic,
            durable: true, autoDelete: false, cancellationToken: cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        // This service has one caller: the publisher worker. StopAsync waits for
        // that worker before closing, so no semaphore is needed for publications.
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        if (channel is not null)
        {
            try
            {
                if (channel.IsOpen)
                    await channel.CloseAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Channel close did not complete gracefully.");
            }
            finally
            {
                try { await channel.DisposeAsync().AsTask().WaitAsync(cancellationToken); }
                catch (Exception exception) { logger.LogDebug(exception, "Channel disposal was interrupted."); }
            }
        }

        if (connection is not null)
        {
            try
            {
                if (connection.IsOpen)
                    await connection.CloseAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Connection close did not complete gracefully.");
            }
            finally
            {
                try { await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken); }
                catch (Exception exception) { logger.LogDebug(exception, "Connection disposal was interrupted."); }
            }
        }
    }
}
