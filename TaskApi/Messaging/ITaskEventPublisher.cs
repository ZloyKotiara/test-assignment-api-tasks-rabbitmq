namespace TaskApi.Messaging;

public interface ITaskEventPublisher
{
    void Enqueue(TaskCompletedEvent message);
}
