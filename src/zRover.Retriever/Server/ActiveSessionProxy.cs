using System.Text.Json;
using zRover.Core;
using zRover.Core.Sessions;
using zRover.Retriever.Sessions;
using zRover.Mcp;

namespace zRover.Retriever.Server;

/// <summary>
/// Owns the dynamic proxy layer between the master MCP server and the active session.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>
///     On every session registration (and on every remote re-publish via
///     <c>tools/list_changed</c>), fetches that session's tool list and registers
///     forwarding proxies for any tool name not yet present in the
///     <see cref="McpToolRegistryAdapter"/>. The catalog is the union of every
///     session's capability set, so federated remote sessions that expose tools
///     local sessions don't (and vice-versa) are all reachable.
///   </item>
///   <item>
///     Every proxy tool delegates to <see cref="ISessionRegistry.ActiveSession"/> at
///     call time — the tool registration itself never changes after initialisation.
///   </item>
///   <item>
///     Maintains a <see cref="CancellationTokenSource"/> scoped to the active session.
///     When the active session changes or disconnects, that CTS is cancelled, which
///     interrupts all in-flight calls and returns an "interrupted" error to the caller.
///   </item>
/// </list>
/// </summary>
public sealed class ActiveSessionProxy
{
    private readonly ISessionRegistry _sessions;
    private readonly McpToolRegistryAdapter _adapter;
    private readonly ILogger<ActiveSessionProxy> _logger;

    // Replaced atomically whenever the active session changes.
    // All in-flight proxy calls hold a reference to their token at dispatch time.
    private volatile CancellationTokenSource _activeCts = new();

    // True once proxy tools have been registered from the first session.
    private bool _toolsInitialised;
    private readonly object _initLock = new();

    /// <summary>
    /// Whether proxy (app-interaction) tools have been registered in the adapter.
    /// Used by external callers to decide whether a <c>tools/list_changed</c>
    /// notification is meaningful at this point in time.
    /// </summary>
    public bool IsInitialized
    {
        get { lock (_initLock) return _toolsInitialised; }
    }

    public ActiveSessionProxy(
        ISessionRegistry sessions,
        McpToolRegistryAdapter adapter,
        ILogger<ActiveSessionProxy> logger)
    {
        _sessions = sessions;
        _adapter = adapter;
        _logger = logger;

        _sessions.ActiveSessionChanged += OnActiveSessionChanged;
    }

    /// <summary>
    /// Called when a session registers (or re-publishes its tool list). Fetches the
    /// session's current tool list and merges any tools not yet present in the
    /// adapter as forwarding proxies.
    ///
    /// This is intentionally idempotent and incremental:
    /// <list type="bullet">
    ///   <item>Sessions can join with disjoint capability sets — the union is published.</item>
    ///   <item>A late-arriving session that exposes a tool no earlier session did
    ///         (e.g. a federated <see cref="PropagatedSession"/> whose remote Manager
    ///         only just gained a capability) contributes that tool to this Manager.</item>
    ///   <item>Schemas of already-registered tools are not overwritten — first writer wins,
    ///         consistent with the original "all sessions share the same SDK" assumption
    ///         in the common case.</item>
    /// </list>
    /// </summary>
    public async Task OnSessionRegisteredAsync(IRoverSession session)
    {
        IReadOnlyList<DiscoveredTool> tools;
        try
        {
            tools = await session.ListToolsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list tools from session {SessionId}", session.SessionId);
            return;
        }

        bool addedAny;
        bool firstInit;
        lock (_initLock)
        {
            // Filter out tools already registered (by the device management layer
            // or by an earlier session). Also handles the race where a WinUI app
            // registers before its MCP server has published any tools — in that
            // case nothing is added and we wait for a later registration to retry.
            var newTools = tools.Where(t => !_adapter.IsToolRegistered(t.Name)).ToList();

            if (newTools.Count == 0)
            {
                if (!_toolsInitialised)
                {
                    _logger.LogWarning(
                        "Session {SessionId} ({DisplayName}) produced no new proxy tools (list was empty or all already registered). Will retry with the next session.",
                        session.SessionId, session.Identity.DisplayName);
                }
                else
                {
                    _logger.LogDebug(
                        "Session {SessionId} ({DisplayName}) added no new tools — adapter already covers its capabilities.",
                        session.SessionId, session.Identity.DisplayName);
                }
                return;
            }

            firstInit = !_toolsInitialised;
            _logger.LogInformation(
                firstInit
                    ? "Initialising proxy tool skeleton from session {SessionId} ({DisplayName}): {Count} new tools ({Total} total from session)"
                    : "Merging additional proxy tools from session {SessionId} ({DisplayName}): {Count} new tools ({Total} total from session)",
                session.SessionId, session.Identity.DisplayName, newTools.Count, tools.Count);

            foreach (var tool in newTools)
            {
                var capturedName = tool.Name;
                _adapter.RegisterTool(tool.Name, tool.Description, tool.InputSchema,
                    (Func<string, Task<RoverToolResult>>)(argsJson =>
                        ProxyInvokeAsync(capturedName, argsJson)));
            }

            _toolsInitialised = true;
            addedAny = true;
        }

        if (addedAny)
            _adapter.NotifyToolsChanged();
    }

    private async Task<RoverToolResult> ProxyInvokeAsync(string toolName, string argsJson)
    {
        var activeSession = _sessions.ActiveSession;
        if (activeSession == null || !activeSession.IsConnected)
            return RoverToolResult.FromText(JsonSerializer.Serialize(new
            {
                error = "no_active_session",
                message = "No active app session is set. Use set_active_app to choose one, then retry."
            }));

        // Capture the CTS for the current active session so that if the session changes
        // mid-call we cancel this invocation, not a future one.
        var sessionCts = _activeCts;

        try
        {
            var raw = await activeSession.InvokeToolAsync(toolName, argsJson, sessionCts.Token);
            var augmentedText = AugmentResult(raw.Text, activeSession);
            return raw.HasImage
                ? RoverToolResult.WithImage(augmentedText, raw.ImageBytes!, raw.ImageMimeType ?? "image/png")
                : RoverToolResult.FromText(augmentedText);
        }
        catch (OperationCanceledException)
        {
            return RoverToolResult.FromText(JsonSerializer.Serialize(new
            {
                error = "interrupted",
                message = "Tool call was interrupted because the active session changed or disconnected. Use set_active_app to set a new active session and retry."
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed on session {SessionId}", toolName, activeSession.SessionId);
            return RoverToolResult.FromText(JsonSerializer.Serialize(new { error = "invocation_failed", message = ex.Message }));
        }
    }

    /// <summary>
    /// Injects a <c>_rover_session</c> key into the result so the MCP client always
    /// knows which app instance handled the call, even if it lost track of the active session.
    /// <list type="bullet">
    ///   <item>If the result is a JSON object, the key is added at the top level.</item>
    ///   <item>Otherwise the original result is preserved under <c>_result</c>.</item>
    /// </list>
    /// </summary>
    private static string AugmentResult(string raw, IRoverSession session)
    {
        object? originNode = null;
        if (session is PropagatedSession ps)
        {
            originNode = new
            {
                type = ps.Origin.Type,
                managerId = ps.Origin.ManagerId,
                managerAlias = ps.Origin.ManagerAlias,
                hops = ps.Origin.Hops
            };
        }

        var sessionNode = new
        {
            sessionId   = session.SessionId,
            appName     = session.Identity.AppName,
            version     = session.Identity.Version,
            instanceId  = session.Identity.InstanceId,
            displayName = session.Identity.DisplayName,
            origin      = originNode
        };

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Merge _rover_session into the existing object
                using var ms = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WritePropertyName("_rover_session");
                JsonSerializer.Serialize(writer, sessionNode);
                writer.WriteEndObject();
                writer.Flush();
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch { /* unparseable — fall through to wrapper */ }

        // Non-object result (array, scalar, raw text): wrap it
        return JsonSerializer.Serialize(new { _result = raw, _rover_session = sessionNode });
    }

    private void OnActiveSessionChanged(object? sender, ActiveSessionChangedEventArgs e)
    {
        // Cancel all in-flight calls from the previous session
        var oldCts = Interlocked.Exchange(ref _activeCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();

        if (e.Current != null)
            _logger.LogInformation("Active session → {DisplayName} (session {SessionId})",
                e.Current.Identity.DisplayName, e.Current.SessionId);
        else if (e.Previous != null)
            _logger.LogWarning("Active session {DisplayName} disconnected — no active session",
                e.Previous.Identity.DisplayName);
    }
}
