using zRover.Core.Sessions;

namespace zRover.BackgroundManager.Sessions;

/// <summary>
/// Thread-safe registry of all connected <see cref="IRoverSession"/> instances.
/// Tracks the active session and fires <see cref="ActiveSessionChanged"/> whenever
/// it changes (explicit set or disconnection).
/// </summary>
public sealed class SessionRegistry : ISessionRegistry
{
    private readonly object _lock = new();
    private readonly List<IRoverSession> _sessions = new();
    private IRoverSession? _active;

    public IReadOnlyList<IRoverSession> Sessions
    {
        get { lock (_lock) return _sessions.ToList(); }
    }

    public IRoverSession? ActiveSession
    {
        get { lock (_lock) return _active; }
    }

    public event EventHandler<ActiveSessionChangedEventArgs>? ActiveSessionChanged;

    public void Add(IRoverSession session)
    {
        lock (_lock)
            _sessions.Add(session);

        session.Disconnected += OnSessionDisconnected;
    }

    public bool Remove(string sessionId)
    {
        IRoverSession? removed;
        IRoverSession? prevActive = null;

        lock (_lock)
        {
            removed = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (removed == null) return false;

            _sessions.Remove(removed);

            if (_active?.SessionId == sessionId)
            {
                prevActive = _active;
                _active = null;
            }
        }

        removed.Disconnected -= OnSessionDisconnected;

        if (prevActive != null)
            ActiveSessionChanged?.Invoke(this, new ActiveSessionChangedEventArgs(prevActive, null));

        return true;
    }

    public bool TrySetActive(string sessionId)
    {
        IRoverSession? prev, next;

        lock (_lock)
        {
            next = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (next == null) return false;

            prev = _active;
            _active = next;
        }

        if (prev?.SessionId != next.SessionId)
            ActiveSessionChanged?.Invoke(this, new ActiveSessionChangedEventArgs(prev, next));

        return true;
    }

    private void OnSessionDisconnected(object? sender, EventArgs e)
    {
        if (sender is IRoverSession session)
            Remove(session.SessionId);
    }
}
