namespace TaskApi.Messaging;

// A narrow seam for testing queue draining without mocking RabbitMQ's entire client.
public interface IRabbitMqTransport
{
    Task PublishAsync(TaskCompletedEvent message, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}
