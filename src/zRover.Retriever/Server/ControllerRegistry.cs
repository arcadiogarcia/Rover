using System.Collections.Concurrent;

namespace zRover.Retriever.Server;

/// <summary>
/// Tracks inbound MCP client connections — remote Retrievers that have connected
/// to this instance to control it through federation.
///
/// Each active SSE stream to the <c>/mcp</c> endpoint is tracked as a "controller".
/// Middleware registers the connection when the SSE response begins and unregisters
/// it when the response completes (client disconnects).
/// </summary>
public sealed class ControllerRegistry
{
    private readonly ConcurrentDictionary<string, ControllerInfo> _controllers = new();

    /// <summary>Fired when a controller connects or disconnects.</summary>
    public event EventHandler? ControllersChanged;

    /// <summary>Snapshot of all currently connected controllers.</summary>
    public IReadOnlyList<ControllerInfo> Controllers =>
        _controllers.Values.OrderBy(c => c.ConnectedSince).ToList();

    /// <summary>
    /// Registers an active inbound MCP session.
    /// Returns a key that must be passed to <see cref="Untrack"/> on disconnect.
    /// </summary>
    public string Track(string remoteAddress)
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        _controllers[key] = new ControllerInfo
        {
            Key = key,
            RemoteAddress = remoteAddress,
            ConnectedSince = DateTimeOffset.UtcNow,
        };
        ControllersChanged?.Invoke(this, EventArgs.Empty);
        return key;
    }

    /// <summary>Removes a previously tracked controller session.</summary>
    public void Untrack(string key)
    {
        if (_controllers.TryRemove(key, out _))
            ControllersChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Read-only snapshot of a single inbound controller connection.</summary>
public record ControllerInfo
{
    public string Key { get; init; } = "";
    public string RemoteAddress { get; init; } = "";
    public DateTimeOffset ConnectedSince { get; init; }
}
