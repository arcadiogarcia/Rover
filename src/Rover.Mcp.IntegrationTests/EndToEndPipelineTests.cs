using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Rover.Mcp.IntegrationTests;

/// <summary>
/// Real end-to-end integration tests that connect to the live MCP server
/// running inside the deployed UWP app package.
///
/// Architecture under test:
///
///   dotnet test (MCP Client)
///       │
///       ├──[HTTP Streamable MCP]──▶  FullTrust MCP Server (Kestrel on :5100)
///       │                                    │
///       │                              [AppService IPC]
///       │                                    │
///       │                                    ▼
///       │                          Real UWP App (ToolRegistry)
///       │                                    │
///       │                                    ▼
///       │                          Actual Tool Handlers
///       │                          (ScreenshotCapability, InputInjectionCapability)
///
/// Prerequisites:
///   1. UWP app is deployed and running (launches FullTrust server on start)
///   2. FullTrust MCP server is listening on http://localhost:5100/mcp
///   3. Run: dotnet test --filter "EndToEndPipelineTests"
///
/// These tests verify the REAL pipeline — every hop from MCP client through
/// HTTP to the FullTrust server, through AppService IPC to the UWP app,
/// to the actual tool handlers and back.
/// </summary>
[Collection("E2E")]
public class EndToEndPipelineTests : IAsyncLifetime
{
    /// <summary>
    /// The endpoint where the FullTrust MCP server is listening.
    /// Override via ROVER_MCP_ENDPOINT environment variable.
    /// </summary>
    private static readonly Uri McpEndpoint = new(
        Environment.GetEnvironmentVariable("ROVER_MCP_ENDPOINT")
        ?? "http://localhost:5100/mcp");

    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = McpEndpoint
        });

        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (_client is IAsyncDisposable d) await d.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Connection & server info
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Server_Responds_WithCorrectInfo()
    {
        _client.ServerInfo.Name.Should().Be("Rover");
        _client.ServerInfo.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Server_Ping_Succeeds()
    {
        await _client.PingAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tool Discovery
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListTools_ReturnsRegisteredUwpTools()
    {
        var tools = await _client.ListToolsAsync();

        // The real UWP app registers 3 tools via ScreenshotCapability + InputInjectionCapability
        tools.Should().HaveCountGreaterOrEqualTo(3,
            "UWP app should register capture_current_view, inject_tap, inject_drag_path");

        var names = tools.Select(t => t.Name).ToList();
        names.Should().Contain("capture_current_view");
        names.Should().Contain("inject_tap");
        names.Should().Contain("inject_drag_path");
    }

    [Fact]
    public async Task ListTools_InjectTap_HasCorrectMetadata()
    {
        var tools = await _client.ListToolsAsync();
        var tap = tools.First(t => t.Name == "inject_tap");

        tap.Description.Should().Contain("tap");
        tap.Description.Should().Contain("coordinates");

        // Verify the schema survived: UWP ToolRegistry → AppService ValueSet JSON →
        // FullTrust server deserialization → MCP SDK → HTTP wire → MCP client
        var props = tap.JsonSchema.GetProperty("properties");
        props.GetProperty("x").GetProperty("type").GetString().Should().Be("number");
        props.GetProperty("y").GetProperty("type").GetString().Should().Be("number");

        var required = tap.JsonSchema.GetProperty("required");
        var requiredNames = Enumerable.Range(0, required.GetArrayLength())
            .Select(i => required[i].GetString()).ToList();
        requiredNames.Should().Contain("x");
        requiredNames.Should().Contain("y");
    }

    [Fact]
    public async Task ListTools_CaptureCurrentView_HasSchema()
    {
        var tools = await _client.ListToolsAsync();
        var screenshot = tools.First(t => t.Name == "capture_current_view");

        screenshot.Description.Should().NotBeNullOrEmpty();

        var schema = screenshot.JsonSchema;
        schema.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task ListTools_InjectDragPath_HasPointsSchema()
    {
        var tools = await _client.ListToolsAsync();
        var drag = tools.First(t => t.Name == "inject_drag_path");

        drag.Description.Should().Contain("drag");

        var props = drag.JsonSchema.GetProperty("properties");
        props.GetProperty("points").GetProperty("type").GetString().Should().Be("array");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tool Invocation — capture_current_view
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CaptureCurrentView_ReturnsResult()
    {
        var result = await _client.CallToolAsync(
            "capture_current_view",
            new Dictionary<string, object?> { { "format", "png" } });

        result.IsError.Should().NotBe(true, "capture_current_view should succeed when UWP app is running");
        result.Content.Should().NotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tool Invocation — inject_tap
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectTap_WithNormalizedCoordinates_Succeeds()
    {
        var result = await _client.CallToolAsync(
            "inject_tap",
            new Dictionary<string, object?>
            {
                { "x", 0.5 },
                { "y", 0.5 },
                { "coordinateSpace", "normalized" }
            });

        result.IsError.Should().NotBe(true, "inject_tap should succeed when app is running");
        result.Content.Should().NotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tool Invocation — inject_drag_path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectDragPath_WithTwoPoints_Succeeds()
    {
        var result = await _client.CallToolAsync(
            "inject_drag_path",
            new Dictionary<string, object?>
            {
                { "points", new[] {
                    new Dictionary<string, object?> { { "x", 0.3 }, { "y", 0.5 } },
                    new Dictionary<string, object?> { { "x", 0.7 }, { "y", 0.5 } }
                }},
                { "durationMs", 300 },
                { "coordinateSpace", "normalized" }
            });

        result.IsError.Should().NotBe(true, "inject_drag_path should succeed when app is running");
        result.Content.Should().NotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error handling
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NonexistentTool_ReturnsError()
    {
        Exception? caught = null;
        try
        {
            await _client.CallToolAsync(
                "nonexistent_tool_xyz",
                new Dictionary<string, object?>());
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("MCP server should reject calls to nonexistent tools");
    }
}
