using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using TaskApi.Messaging;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace TaskApi.Tests;

[CollectionDefinition("Infrastructure", DisableParallelization = true)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("task_api_tests")
        .WithUsername("taskapi")
        .WithPassword("taskapi")
        .Build();

    public RabbitMqContainer RabbitMq { get; } = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithUsername("taskapi")
        .WithPassword("taskapi")
        .Build();

    public Task InitializeAsync() => Task.WhenAll(Postgres.StartAsync(), RabbitMq.StartAsync());

    public async Task DisposeAsync()
    {
        await RabbitMq.DisposeAsync();
        await Postgres.DisposeAsync();
    }

    public Task<IConnection> ConnectRabbitAsync() => new ConnectionFactory
    {
        HostName = RabbitMq.Hostname,
        Port = RabbitMq.GetMappedPublicPort(5672),
        UserName = "taskapi",
        Password = "taskapi"
    }.CreateConnectionAsync();

    public TaskApiFactory CreateFactory(
        Action<IServiceCollection>? configureServices = null,
        Dictionary<string, string?>? overrides = null) =>
        new(this, configureServices, overrides);
}

public sealed class TaskApiFactory(
    InfrastructureFixture infrastructure,
    Action<IServiceCollection>? configureServices = null,
    Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Tasks"] = infrastructure.Postgres.GetConnectionString(),
                ["RabbitMq:HostName"] = infrastructure.RabbitMq.Hostname,
                ["RabbitMq:Port"] = infrastructure.RabbitMq.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:UserName"] = "taskapi",
                ["RabbitMq:Password"] = "taskapi",
                ["RabbitMq:PublishTimeoutSeconds"] = "1"
            };
            if (overrides is not null)
                foreach (var pair in overrides)
                    values[pair.Key] = pair.Value;
            configuration.AddInMemoryCollection(values);
        });
        if (configureServices is not null)
            builder.ConfigureServices(configureServices);
    }
}

public sealed class RecordingPublisher : ITaskEventPublisher
{
    public ConcurrentQueue<TaskCompletedEvent> Events { get; } = new();
    public void Enqueue(TaskCompletedEvent message) => Events.Enqueue(message);
}

public sealed class TestClock(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan amount) => _now += amount;
}

public sealed class LogCollector : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public TaskCompletionSource<string> PublicationFailure { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ILogger CreateLogger(string categoryName) => new CollectorLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CollectorLogger(LogCollector collector, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            collector.Messages.Enqueue(message);
            if (category == typeof(RabbitMqPublisher).FullName && level == LogLevel.Error)
                collector.PublicationFailure.TrySetResult(message);
        }
    }
}
