using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TaskApi.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class CompleteTaskIntegrationTest(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Create_then_complete_persists_timestamp_and_publishes_expected_message()
    {
        await using var connection = await infrastructure.ConnectRabbitAsync();
        await using var channel = await connection.CreateChannelAsync();

        // The app connects lazily, so the test declares the exchange before binding.
        await channel.ExchangeDeclareAsync("task.events", ExchangeType.Topic, durable: true);
        var queue = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue.QueueName, "task.events", "task.completed");

        var received = new TaskCompletionSource<(byte[] Body, string Exchange, string RoutingKey)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            // RabbitMQ's memory belongs to the callback. Copy before returning.
            received.TrySetResult((delivery.Body.ToArray(), delivery.Exchange, delivery.RoutingKey));
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer);

        var clock = new TestClock(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
        await using var factory = infrastructure.CreateFactory(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var client = factory.CreateClient();
        using var create = await client.PostAsJsonAsync("/tasks",
            new CreateTaskRequest("  Buy milk  ", Priority.High), TaskJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var task = (await create.Content.ReadFromJsonAsync<TaskResponse>(TaskJson.Options))!;
        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
        Assert.Equal("Buy milk", task.Title);

        clock.Advance(TimeSpan.FromMinutes(3));
        using var complete = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var delivery = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("task.events", delivery.Exchange);
        Assert.Equal("task.completed", delivery.RoutingKey);
        var message = JsonSerializer.Deserialize<TaskCompletedEvent>(delivery.Body, TaskJson.Options)!;
        Assert.Equal(task.Id, message.TaskId);
        Assert.Equal(task.Title, message.Title);
        Assert.Equal(Priority.High, message.Priority);
        Assert.Equal(clock.GetUtcNow(), message.CompletedAt);
        using var document = JsonDocument.Parse(delivery.Body);
        Assert.Equal("High", document.RootElement.GetProperty("priority").GetString());

        var all = await client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskJson.Options);
        var saved = Assert.Single(all!, x => x.Id == task.Id);
        Assert.True(saved.IsCompleted);
        Assert.Equal(message.CompletedAt, saved.CompletedAt);
        Assert.Equal(task.CreatedAt, saved.CreatedAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        using var repeat = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        all = await client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskJson.Options);
        Assert.Equal(saved.CompletedAt, Assert.Single(all!, x => x.Id == task.Id).CompletedAt);
    }
}
