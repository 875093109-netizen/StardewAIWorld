namespace AIWorld.Core.Chat;

public enum ChatRequestPhase { Queued, Preparing, Generating, Validating, Completed, Expired, Failed, Cancelled }

/// <summary>Finite request lifecycle. A delayed model response never authorizes an action in a different world snapshot.</summary>
public sealed class ChatRequestLifecycle
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ChatRequestPhase Phase { get; private set; } = ChatRequestPhase.Queued;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public void MoveTo(ChatRequestPhase next)
    {
        bool valid = Phase switch
        {
            ChatRequestPhase.Queued => next is ChatRequestPhase.Preparing or ChatRequestPhase.Cancelled or ChatRequestPhase.Expired,
            ChatRequestPhase.Preparing => next is ChatRequestPhase.Generating or ChatRequestPhase.Completed or ChatRequestPhase.Failed or ChatRequestPhase.Cancelled or ChatRequestPhase.Expired,
            ChatRequestPhase.Generating => next is ChatRequestPhase.Validating or ChatRequestPhase.Failed or ChatRequestPhase.Cancelled or ChatRequestPhase.Expired,
            ChatRequestPhase.Validating => next is ChatRequestPhase.Completed or ChatRequestPhase.Expired or ChatRequestPhase.Failed or ChatRequestPhase.Cancelled,
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Illegal chat transition {Phase} -> {next}");
        Phase = next;
    }
    public static bool IsCurrent(object? preparedSave, object? currentSave, string preparedState, string currentState,
        DateTimeOffset preparedAt, DateTimeOffset now) => ReferenceEquals(preparedSave, currentSave)
        && preparedSave is not null && preparedState == currentState && now - preparedAt <= TimeSpan.FromSeconds(90);
}
