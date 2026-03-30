using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.BackgroundManager;
using zRover.BackgroundManager.Server;
using zRover.BackgroundManager.Sessions;
using zRover.Core.Sessions;
using zRover.Mcp;

var builder = WebApplication.CreateBuilder(args);

// ── Core services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());
builder.Services.AddSingleton<McpToolRegistryAdapter>();
builder.Services.AddSingleton<ActiveSessionProxy>();
builder.Services.AddHostedService<Worker>();

// ── Master MCP server ──────────────────────────────────────────────────────
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation { Name = "zRover.Manager", Version = "1.0.0" };
    options.Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability { ListChanged = true }
    };
    // ToolCollection is wired after build via the adapter
}).WithHttpTransport();

var app = builder.Build();

// ── Initialise management tools in the adapter ─────────────────────────────
var adapter  = app.Services.GetRequiredService<McpToolRegistryAdapter>();
var sessions = app.Services.GetRequiredService<ISessionRegistry>();
SessionManagementTools.Register(adapter, sessions);

// Patch the MCP server options to use the live tool collection
// (done after build so DI is available)
var mcpOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;
mcpOptions.ToolCollection = adapter.Tools;

// ── Session registration endpoint ─────────────────────────────────────────
// Per-app zRover.FullTrust.McpServer POSTs here when it starts:
//   POST /sessions/register
//   Body: { appName, version, instanceId?, mcpUrl }
app.MapPost("/sessions/register", async (
    SessionRegistrationRequest req,
    SessionRegistry registry,
    ActiveSessionProxy proxy,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AppName) || string.IsNullOrWhiteSpace(req.McpUrl))
        return Results.BadRequest(new { error = "appName and mcpUrl are required" });

    var sessionId = Guid.NewGuid().ToString("N")[..12];
    var identity  = new RoverAppIdentity(req.AppName, req.Version, req.InstanceId);

    log.LogInformation("Session registering: {DisplayName} at {McpUrl}", identity.DisplayName, req.McpUrl);

    McpClientSession session;
    try
    {
        session = await McpClientSession.ConnectAsync(sessionId, identity, req.McpUrl, ct);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to connect MCP client to {McpUrl}", req.McpUrl);
        return Results.Problem($"Could not connect to MCP server at {req.McpUrl}: {ex.Message}");
    }

    registry.Add(session);
    log.LogInformation("Session registered: {SessionId} {DisplayName}", sessionId, identity.DisplayName);

    // If this is the first session, initialise the proxy tool skeleton
    await proxy.OnSessionRegisteredAsync(session);

    return Results.Ok(new SessionRegistrationResponse { SessionId = sessionId });
});

app.MapMcp("/mcp");

app.Run();

