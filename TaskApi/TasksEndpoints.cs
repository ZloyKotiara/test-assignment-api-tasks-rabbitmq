using Microsoft.EntityFrameworkCore;
using TaskApi.Messaging;

namespace TaskApi;

public static class TasksEndpoints
{
    public static void MapTasksEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tasks = endpoints.MapGroup("/tasks").WithTags("Tasks");
        tasks.MapPost("", Create)
            .WithName("CreateTask")
            .WithSummary("Создать задачу")
            .WithDescription("Title: от 1 до 200 символов после удаления пробелов по краям. Priority по умолчанию — Medium. Допустимы Low, Medium, High.")
            .Produces<TaskResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        tasks.MapGet("", GetAll)
            .WithName("GetTasks")
            .WithSummary("Получить все задачи")
            .WithDescription("Без пагинации, в порядке CreatedAt и Id.")
            .Produces<TaskResponse[]>();
        tasks.MapPut("/{id}/complete", Complete)
            .WithName("CompleteTask")
            .WithSummary("Завершить задачу")
            .WithDescription("Сохраняет CompletedAt, затем ставит событие в очередь публикации RabbitMQ. Повторное или конкурентное завершение возвращает 409.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<TaskErrorResponse>(StatusCodes.Status409Conflict);
        tasks.MapDelete("/{id}", Delete)
            .WithName("DeleteTask")
            .WithSummary("Удалить задачу")
            .WithDescription("Физически удаляет задачу без восстановления.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> Create(
        CreateTaskRequest request, TaskDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Title must contain between 1 and 200 characters after trimming."]
            });
        }

        if (!Enum.IsDefined(request.Priority))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["priority"] = ["Priority must be Low, Medium, or High."]
            });
        }

        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            Priority = request.Priority,
            IsCompleted = false,
            CreatedAt = UtcNow(clock),
            CompletedAt = null
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        // The assignment exposes the collection, so Location points to that existing GET route.
        return Results.Created("/tasks", TaskResponse.From(task));
    }

    private static async Task<IResult> GetAll(TaskDbContext db, CancellationToken cancellationToken)
    {
        var tasks = await db.Tasks.AsNoTracking()
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new TaskResponse(x.Id, x.Title, x.IsCompleted, x.CreatedAt, x.CompletedAt, x.Priority))
            .ToListAsync(cancellationToken);
        return Results.Ok(tasks);
    }

    private static async Task<IResult> Complete(
        Guid id, TaskDbContext db, TimeProvider clock, ITaskEventPublisher publisher,
        CancellationToken cancellationToken)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
            return Results.NotFound();

        if (task.IsCompleted)
            return Results.Conflict(new TaskErrorResponse("Task is already completed."));

        task.IsCompleted = true;
        task.CompletedAt = UtcNow(clock);

        try
        {
            // The tracked xmin is included in UPDATE's WHERE clause.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new TaskErrorResponse("Task was changed or deleted by another request."));
        }

        // Only the successful database writer gets here. Do not pass RequestAborted:
        // disconnecting the HTTP client cannot undo an already committed completion.
        publisher.Enqueue(new TaskCompletedEvent(task.Id, task.Title, task.CompletedAt.Value, task.Priority));
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(Guid id, TaskDbContext db, CancellationToken cancellationToken)
    {
        // No version precondition is needed for an unconditional hard delete.
        var deleted = await db.Tasks.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    // PostgreSQL timestamps have microsecond precision. Normalize once so the saved
    // value and the event are exactly equal after a database round trip.
    private static DateTimeOffset UtcNow(TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
