using System.ComponentModel.DataAnnotations;

namespace TaskApi.Messaging;

public sealed class RabbitMqOptions
{
    [Required] public string HostName { get; set; } = "localhost";
    [Range(1, 65535)] public int Port { get; set; } = 5672;
    [Required] public string UserName { get; set; } = "taskapi";
    [Required] public string Password { get; set; } = "taskapi";
    [Required] public string VirtualHost { get; set; } = "/";
    [Required] public string Exchange { get; set; } = "task.events";
    [Required] public string RoutingKey { get; set; } = "task.completed";
    [Range(1, 100_000)] public int QueueCapacity { get; set; } = 1000;
    [Range(1, 30)] public int PublishTimeoutSeconds { get; set; } = 2;
    [Range(2, 5)] public int ShutdownTimeoutSeconds { get; set; } = 5;
}
