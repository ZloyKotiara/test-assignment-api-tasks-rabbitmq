using Microsoft.EntityFrameworkCore;

namespace TaskApi;

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();
        task.ToTable("tasks", table =>
        {
            table.HasCheckConstraint("CK_tasks_completion",
                "\"IsCompleted\" = (\"CompletedAt\" IS NOT NULL)");
            table.HasCheckConstraint("CK_tasks_priority", "\"Priority\" BETWEEN 0 AND 2");
        });
        task.HasKey(x => x.Id);
        task.Property(x => x.Title).IsRequired().HasMaxLength(200);
        task.Property(x => x.RowVersion).IsRowVersion();
    }
}
