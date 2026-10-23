using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using TaskApi;
using TaskApi.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Task API",
        Version = "v1",
        Description = "Управление задачами. При завершении событие публикуется в RabbitMQ."
    });
});
builder.Services.ConfigureHttpJsonOptions(options => TaskJson.Configure(options.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Tasks")
        ?? throw new InvalidOperationException("ConnectionStrings:Tasks is required.")));

builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration("RabbitMq")
    .ValidateDataAnnotations()
    .Validate(options => options.ShutdownTimeoutSeconds >= 2, "Shutdown timeout must allow cleanup.")
    .ValidateOnStart();

builder.Services.AddSingleton<IRabbitMqTransport, RabbitMqTransport>();
builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddSingleton<ITaskEventPublisher>(services => services.GetRequiredService<RabbitMqPublisher>());
builder.Services.AddHostedService(services => services.GetRequiredService<RabbitMqPublisher>());

// Kestrel drains already accepted requests before the publisher is stopped.
// Leave a larger host budget than the publisher's own five-second budget.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

var app = builder.Build();
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("v1/swagger.json", "Task API v1");
    options.DocumentTitle = "Task API — Swagger";
});
app.MapTasksEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health")
    .WithSummary("Проверка работающего HTTP-процесса");

if (app.Configuration.GetValue("Database:ApplyMigrations", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();

// Required by WebApplicationFactory<Program>.
public partial class Program;
