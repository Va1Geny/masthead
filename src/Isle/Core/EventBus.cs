namespace Isle.Core;

/// <summary>Minimal typed publish/subscribe between modules and views. Handlers run on the publisher's thread.</summary>
public static class EventBus
{
    static readonly Dictionary<Type, List<Delegate>> Handlers = [];
    static readonly object Gate = new();

    public static IDisposable Subscribe<T>(Action<T> handler)
    {
        lock (Gate)
        {
            if (!Handlers.TryGetValue(typeof(T), out var list)) Handlers[typeof(T)] = list = [];
            list.Add(handler);
        }
        return new Unsubscriber(() =>
        {
            lock (Gate)
            {
                if (Handlers.TryGetValue(typeof(T), out var list)) list.Remove(handler);
            }
        });
    }

    public static void Publish<T>(T message)
    {
        Delegate[] snapshot;
        lock (Gate)
        {
            if (!Handlers.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
            snapshot = [.. list];
        }
        foreach (var d in snapshot)
        {
            try { ((Action<T>)d)(message); }
            catch (Exception ex) { Logger.Error($"event handler for {typeof(T).Name}", ex); }
        }
    }

    sealed class Unsubscriber(Action dispose) : IDisposable
    {
        Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
