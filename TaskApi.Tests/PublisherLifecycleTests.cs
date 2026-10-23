using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskApi.Messaging;

namespace TaskApi.Tests;

[Trait("Category", "Publisher")]
public sealed class PublisherLifecycleTests
{
    [Fact]
    public async Task Stop_remains_safe_when_the_host_has_already_called_Dispose()
    {
        var transport = new ControlledTransport();
        using var publisher = Create(transport);
        await publisher.StartAsync(CancellationToken.None);
        publisher.Dispose();
        await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(transport.Closed);
        Assert.Equal(0, transport.ActiveAtClose);
    }

    [Fact]
    public async Task Shutdown_drains_accepted_events_before_closing_the_transport()
    {
        var transport = new ControlledTransport();
        using var publisher = Create(transport);
        publisher.Enqueue(Message("first"));
        publisher.Enqueue(Message("second"));
        await publisher.StartAsync(CancellationToken.None);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stopping = publisher.StopAsync(CancellationToken.None);
        Assert.False(transport.Closed);
        transport.Release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(6));

        Assert.Equal(["first", "second"], transport.Published.Select(x => x.Title).ToArray());
        Assert.True(transport.Closed);
        Assert.Equal(0, transport.ActiveAtClose);
    }

    [Fact]
    public async Task Shutdown_cancels_a_stuck_publication_within_its_budget()
    {
        var transport = new ControlledTransport();
        using var publisher = Create(transport, shutdownSeconds: 2);
        publisher.Enqueue(Message("blocked"));
        await publisher.StartAsync(CancellationToken.None);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var timer = Stopwatch.StartNew();
        await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4));
        Assert.Empty(transport.Published);
        Assert.True(transport.Closed);
        Assert.Equal(0, transport.ActiveAtClose);
    }

    [Fact]
    public async Task Full_and_stopped_queues_log_rejected_events()
    {
        var logs = new LogCollector();
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(logs));
        var transport = new ControlledTransport();
        transport.Release.TrySetResult();
        using var publisher = new RabbitMqPublisher(transport,
            Options.Create(new RabbitMqOptions { QueueCapacity = 1 }),
            loggerFactory.CreateLogger<RabbitMqPublisher>());

        publisher.Enqueue(Message("accepted"));
        publisher.Enqueue(Message("queue-full"));
        await publisher.StartAsync(CancellationToken.None);
        await publisher.StopAsync(CancellationToken.None);
        publisher.Enqueue(Message("already-stopped"));

        Assert.Single(transport.Published);
        Assert.Contains(logs.Messages, x => x.Contains("queue-full"));
        Assert.Contains(logs.Messages, x => x.Contains("already-stopped"));
    }

    [Fact]
    public async Task A_publication_failure_is_logged_and_the_next_event_is_processed()
    {
        var transport = new ControlledTransport { FailFirst = true };
        transport.Release.TrySetResult();
        var logs = new LogCollector();
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var publisher = new RabbitMqPublisher(transport, Options.Create(new RabbitMqOptions()),
            loggerFactory.CreateLogger<RabbitMqPublisher>());
        publisher.Enqueue(Message("failed"));
        publisher.Enqueue(Message("next"));
        await publisher.StartAsync(CancellationToken.None);
        await publisher.StopAsync(CancellationToken.None);

        Assert.Equal("next", Assert.Single(transport.Published).Title);
        Assert.Contains(logs.Messages, x => x.Contains("\"title\":\"failed\""));
        Assert.True(transport.Closed);
    }

    private static RabbitMqPublisher Create(IRabbitMqTransport transport, int shutdownSeconds = 5) =>
        new(transport, Options.Create(new RabbitMqOptions
        {
            PublishTimeoutSeconds = 30,
            ShutdownTimeoutSeconds = shutdownSeconds
        }), NullLogger<RabbitMqPublisher>.Instance);

    private static TaskCompletedEvent Message(string title) =>
        new(Guid.NewGuid(), title, DateTimeOffset.UtcNow, Priority.Medium);

    private sealed class ControlledTransport : IRabbitMqTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<TaskCompletedEvent> Published { get; } = new();
        public bool Closed { get; private set; }
        public int ActiveAtClose { get; private set; }
        public bool FailFirst { get; init; }
        private int _active;
        private int _attempts;

        public async Task PublishAsync(TaskCompletedEvent message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _active);
            try
            {
                Started.TrySetResult();
                if (Interlocked.Increment(ref _attempts) == 1 && FailFirst)
                    throw new IOException("Simulated network error.");
                await Release.Task.WaitAsync(cancellationToken);
                Published.Enqueue(message);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            ActiveAtClose = Volatile.Read(ref _active);
            Closed = true;
            return Task.CompletedTask;
        }
    }
}
