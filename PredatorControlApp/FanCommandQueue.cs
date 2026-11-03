namespace PredatorControlApp;

internal enum FanCommandKind { Mode, Speeds, Cpu, Gpu, System }

internal sealed class FanCommandQueue
{
    private readonly object _sync = new();
    private readonly LinkedList<(FanCommandKind Kind, Action Apply, TaskCompletionSource Completion)> _pending = new();
    private bool _running;
    private bool _disposed;

    internal Task Enqueue(FanCommandKind kind, Action apply)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_disposed)
            {
                completion.TrySetCanceled();
                return completion.Task;
            }

            for (var node = _pending.First; node != null;)
            {
                var next = node.Next;
                if (kind == FanCommandKind.Mode || (kind != FanCommandKind.Speeds && node.Value.Kind == kind))
                {
                    node.Value.Completion.TrySetResult();
                    _pending.Remove(node);
                }
                node = next;
            }
            _pending.AddLast((kind, apply, completion));
            if (!_running)
            {
                _running = true;
                _ = Task.Run(Drain);
            }
        }
        return completion.Task;
    }

    internal void ClearAndStop()
    {
        lock (_sync)
        {
            _disposed = true;
            for (var node = _pending.First; node != null; node = node.Next)
            {
                node.Value.Completion.TrySetCanceled();
            }
            _pending.Clear();
        }
    }

    private void Drain()
    {
        while (true)
        {
            (FanCommandKind Kind, Action Apply, TaskCompletionSource Completion) item;
            lock (_sync)
            {
                if (_disposed || _pending.First == null) { _running = false; return; }
                item = _pending.First.Value;
                _pending.RemoveFirst();
            }
            try { item.Apply(); item.Completion.TrySetResult(); }
            catch (Exception ex) { item.Completion.TrySetException(ex); }
        }
    }
}
