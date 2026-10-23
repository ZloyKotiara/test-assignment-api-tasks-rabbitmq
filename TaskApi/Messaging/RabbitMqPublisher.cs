using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TaskApi.Messaging;

public sealed class RabbitMqPublisher : BackgroundService, ITaskEventPublisher
{
    private readonly Channel<TaskCompletedEvent> _queue;
    private readonly IRabbitMqTransport _transport;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly RabbitMqOptions _options;
    private readonly CancellationTokenSource _abort = new();
    private CancellationToken _shutdownDeadline;
    private int _disposed;

    public RabbitMqPublisher(
        IRabbitMqTransport transport, IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        _transport = transport;
        _options = options.Value;
        _logger = logger;
        _queue = Channel.CreateBounded<TaskCompletedEvent>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public void Enqueue(TaskCompletedEvent message)
    {
        if (!_queue.Writer.TryWrite(message))
            LogUnsent(message, "Local publication queue is full or stopping.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The base stopping token is cancelled at the beginning of StopAsync.
        // Our separate token allows draining first, with forced cancellation later.
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(_abort.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.PublishTimeoutSeconds));
                try
                {
                    await _transport.PublishAsync(message, timeout.Token);
                }
                catch (Exception exception)
                {
                    // The commit already succeeded. A broker error cannot undo it.
                    _logger.LogError(exception,
                        "RabbitMQ publication failed or delivery is unknown. Event={Event}",
                        JsonSerializer.Serialize(message, TaskJson.Options));
                }

                if (_abort.IsCancellationRequested)
                    break;
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            // Shutdown's drain budget expired.
        }
        finally
        {
            while (_queue.Reader.TryRead(out var message))
                LogUnsent(message, "Shutdown reached before publication.");

            // The worker owns both publishing and closing, including on timeout.
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(_shutdownDeadline);
            cleanup.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await _transport.CloseAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
                _logger.LogWarning("RabbitMQ cleanup reached the shutdown deadline.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "RabbitMQ cleanup failed.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds));
        _shutdownDeadline = deadline.Token;
        _queue.Writer.TryComplete();

        // Reserve one second of the SAME overall budget for resource cleanup.
        _abort.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds - 1));
        using var abortWhenHostStopsWaiting = deadline.Token.Register(() => _abort.Cancel());
        try
        {
            if (ExecuteTask is not null)
                await ExecuteTask.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _logger.LogWarning("Publication worker reached the shutdown deadline.");
        }
        finally
        {
            _abort.Cancel();
            await base.StopAsync(deadline.Token);
        }
    }

    private void LogUnsent(TaskCompletedEvent message, string reason) =>
        _logger.LogWarning("{Reason} Event={Event}", reason,
            JsonSerializer.Serialize(message, TaskJson.Options));

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _abort.Cancel();
        base.Dispose();
        // Like BackgroundService itself, cancel without disposing this source:
        // host shutdown and async WebApplicationFactory disposal may overlap.
        // A late StopAsync must still be able to call Cancel/CancelAfter.
    }
}
