using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TaskApi.Messaging;

namespace TaskApi.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class ApiIntegrationTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Title_limit_defaults_missing_ids_and_hard_delete_work()
    {
        await using var factory = infrastructure.CreateFactory();
        using var client = factory.CreateClient();

        using var tooLong = await client.PostAsJsonAsync("/tasks", new { title = new string('a', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var created = await client.PostAsJsonAsync("/tasks", new { title = new string('a', 200) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = (await created.Content.ReadFromJsonAsync<TaskResponse>(TaskJson.Options))!;
        Assert.Equal(Priority.Medium, task.Priority);
        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);

        using var malformed = await client.PutAsync("/tasks/not-a-guid/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var missing = await client.PutAsync($"/tasks/{Guid.NewGuid()}/complete", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var deleted = await client.DeleteAsync($"/tasks/{task.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var all = await client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskJson.Options);
        Assert.DoesNotContain(all!, x => x.Id == task.Id);
        using var deletedAgain = await client.DeleteAsync($"/tasks/{task.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task Concurrent_completions_use_xmin_and_only_the_winner_enqueues_an_event()
    {
        var gate = new CompletionBarrier();
        var publisher = new RecordingPublisher();
        await using var factory = infrastructure.CreateFactory(services =>
        {
            services.AddDbContext<TaskDbContext>(options => options.AddInterceptors(gate));
            services.RemoveAll<ITaskEventPublisher>();
            services.AddSingleton<ITaskEventPublisher>(publisher);
        });
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/tasks", new { title = "Concurrent completion" });
        var task = (await created.Content.ReadFromJsonAsync<TaskResponse>(TaskJson.Options))!;

        // The barrier ensures BOTH requests read the incomplete row before either UPDATE.
        var responses = await Task.WhenAll(
            client.PutAsync($"/tasks/{task.Id}/complete", null),
            client.PutAsync($"/tasks/{task.Id}/complete", null));
        try
        {
            Assert.Equal(2, gate.Arrivals);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(task.Id, Assert.Single(publisher.Events).TaskId);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Failed_database_save_does_not_publish_or_complete_the_task()
    {
        var publisher = new RecordingPublisher();
        await using var factory = infrastructure.CreateFactory(services =>
        {
            services.AddDbContext<TaskDbContext>(options => options.AddInterceptors(new FailCompletionSave()));
            services.RemoveAll<ITaskEventPublisher>();
            services.AddSingleton<ITaskEventPublisher>(publisher);
        });
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/tasks", new { title = "Database failure" });
        var task = (await created.Content.ReadFromJsonAsync<TaskResponse>(TaskJson.Options))!;
        using var completed = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.InternalServerError, completed.StatusCode);
        Assert.Empty(publisher.Events);
        var all = await client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskJson.Options);
        var unchanged = Assert.Single(all!, x => x.Id == task.Id);
        Assert.False(unchanged.IsCompleted);
        Assert.Null(unchanged.CompletedAt);
    }

    [Fact]
    public async Task Unreachable_RabbitMQ_keeps_completion_and_logs_the_full_event()
    {
        var logs = new LogCollector();
        await using var factory = infrastructure.CreateFactory(
            services => services.AddLogging(logging => logging.AddProvider(logs)),
            new Dictionary<string, string?>
            {
                ["RabbitMq:HostName"] = "127.0.0.1",
                ["RabbitMq:Port"] = "1"
            });
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/tasks", new { title = "Broker unavailable" });
        var task = (await created.Content.ReadFromJsonAsync<TaskResponse>(TaskJson.Options))!;
        using var completed = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        var log = await logs.PublicationFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(task.Id.ToString(), log);
        Assert.Contains(task.Title, log);
        Assert.Contains("\"completedAt\"", log);
        Assert.Contains("\"priority\":\"Medium\"", log);

        var all = await client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskJson.Options);
        var saved = Assert.Single(all!, x => x.Id == task.Id);
        Assert.True(saved.IsCompleted);
        Assert.NotNull(saved.CompletedAt);
    }

    private sealed class CompletionBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskItem>()
                .Any(x => x.State == EntityState.Modified && x.Entity.IsCompleted))
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                    _bothArrived.TrySetResult();
                await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailCompletionSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TaskItem>().Any(x => x.State == EntityState.Modified))
                throw new DbUpdateException("Simulated database save failure.");
            return ValueTask.FromResult(result);
        }
    }
}
