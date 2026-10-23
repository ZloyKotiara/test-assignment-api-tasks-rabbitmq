namespace TaskApi;

public enum Priority
{
    Low,
    Medium,
    High
}

public sealed class TaskItem
{
    public Guid Id { get; set; } //It is better to use v7, but standard v4 will also work for this solution.
    public required string Title { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;

    // Npgsql maps uint + IsRowVersion() to PostgreSQL's system column xmin.
    public uint RowVersion { get; set; }
}
