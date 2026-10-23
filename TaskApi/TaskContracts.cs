using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskApi;

public sealed record CreateTaskRequest(string? Title, Priority Priority = Priority.Medium);

public sealed record TaskErrorResponse(string Error);

public sealed record TaskResponse(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    Priority Priority)
{
    public static TaskResponse From(TaskItem task) =>
        new(task.Id, task.Title, task.IsCompleted, task.CreatedAt, task.CompletedAt, task.Priority);
}

public sealed record TaskCompletedEvent(
    Guid TaskId,
    string Title,
    DateTimeOffset CompletedAt,
    Priority Priority);

// Explicitly shared by the HTTP API, RabbitMQ payloads, and tests.
public static class TaskJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static void Configure(JsonSerializerOptions options) =>
        options.Converters.Add(new JsonStringEnumConverter<Priority>(allowIntegerValues: false));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
