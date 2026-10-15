using System.Collections.Concurrent;

namespace AIWorld.Core.Chat;

/// <summary>Marshal game-state access to the host's update loop without blocking it.</summary>
public sealed class MainThreadWorkQueue : IDisposable
{
    private readonly ConcurrentQueue<(Action Run, Action Cancel)> _pending = new();
    private readonly object _gate = new();
    private bool _disposed;

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(MainThreadWorkQueue)));
            _pending.Enqueue((() =>
            {
                try { completion.TrySetResult(work()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }, () => completion.TrySetException(new ObjectDisposedException(nameof(MainThreadWorkQueue)))));
        }
        return completion.Task;
    }

    public void Drain()
    {
        while (_pending.TryDequeue(out var item)) item.Run();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_pending.TryDequeue(out var item)) item.Cancel();
        }
    }
}
