using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rover.Core;
using Rover.Mcp;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace Rover.FullTrust.McpServer;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Error.WriteLine("[McpServer] Starting Rover FullTrust MCP Server...");

        // Default mode: HTTP on port 5100 (can be overridden with --port)
        var port = 5100;
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p))
            port = p;

        var useStdio = args.Contains("--stdio");

        // Connect to the UWP AppService (retries built in)
        var backend = new AppServiceToolBackend();
        await backend.ConnectAsync();
        Console.Error.WriteLine("[McpServer] Connected to UWP AppService");

        // Discover tools from the UWP app upfront
        var adapter = new McpToolRegistryAdapter();
        var tools = await backend.ListToolsAsync();
        Console.Error.WriteLine($"[McpServer] Discovered {tools.Count} tools");

        foreach (var tool in tools)
        {
            Console.Error.WriteLine($"[McpServer] Registering proxy tool: {tool.Name}");
            var capturedName = tool.Name;
            adapter.RegisterTool(tool.Name, tool.Description, tool.InputSchema, async argsJson =>
            {
                return await backend.InvokeToolAsync(capturedName, argsJson);
            });
        }

        if (useStdio)
        {
            Console.Error.WriteLine("[McpServer] Running in stdio mode");
            await McpServerRunner.RunStdioAsync(adapter, Console.OpenStandardInput(), Console.OpenStandardOutput());
        }
        else
        {
            Console.Error.WriteLine($"[McpServer] Running HTTP server on port {port}");
            await McpServerRunner.RunHttpAsync(adapter, port);
        }

        Console.Error.WriteLine("[McpServer] MCP server shutting down");
        backend.Dispose();
    }
}

/// <summary>
/// Reusable server setup: runs the MCP server over stdio or HTTP.
/// Tools are pre-registered in the <see cref="McpToolRegistryAdapter"/> by the caller.
/// </summary>
internal static class McpServerRunner
{
    /// <summary>Runs the MCP server over stdin/stdout (classic FullTrustProcess mode).</summary>
    public static async Task RunStdioAsync(
        McpToolRegistryAdapter adapter,
        System.IO.Stream input,
        System.IO.Stream output,
        CancellationToken cancellationToken = default)
    {
        var options = CreateOptions(adapter);

        await using var transport = new StreamServerTransport(input, output, "Rover");
        var server = ModelContextProtocol.Server.McpServer.Create(transport, options);

        Console.Error.WriteLine("[McpServer] MCP server running (stdio)");
        await server.RunAsync(cancellationToken);
    }

    /// <summary>Runs the MCP server as an HTTP endpoint using ASP.NET Core + MapMcp().</summary>
    public static async Task RunHttpAsync(
        McpToolRegistryAdapter adapter,
        int port,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new string[] { "--urls", $"http://localhost:{port}" });

        // Register our pre-discovered tools as singleton services
        foreach (var tool in adapter.Tools)
        {
            builder.Services.AddSingleton(tool);
        }

        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "Rover", Version = "1.0.0" };
            options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true }
            };
        }).WithHttpTransport();

        var app = builder.Build();
        app.MapMcp("/mcp");

        Console.Error.WriteLine($"[McpServer] MCP server running on http://localhost:{port}/mcp");
        await app.RunAsync();
    }

    private static McpServerOptions CreateOptions(McpToolRegistryAdapter adapter)
    {
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Rover", Version = "1.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true }
            },
            ToolCollection = adapter.Tools
        };
    }
}

/// <summary>
/// Real <see cref="IToolBackend"/> that communicates with the UWP app via
/// <see cref="AppServiceConnection"/> (same-package IPC).
/// </summary>
internal sealed class AppServiceToolBackend : IToolBackend, IDisposable
{
    private AppServiceConnection? _connection;

    public async Task ConnectAsync()
    {
        // Log to a file in the package's LocalState so we can diagnose issues
        var logPath = System.IO.Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            "fulltrust-server.log");

        void Log(string msg)
        {
            var line = $"{DateTimeOffset.Now:o} {msg}";
            Console.Error.WriteLine($"[McpServer] {msg}");
            try { System.IO.File.AppendAllText(logPath, line + "\r\n"); } catch { }
        }

        var familyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
        Log($"PackageFamilyName: {familyName}");

        // The UWP app may not have its AppService ready immediately when we start.
        // Retry with backoff to handle the race condition.
        const int maxRetries = 15;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _connection = new AppServiceConnection
            {
                AppServiceName = "com.rover.toolinvocation",
                PackageFamilyName = familyName
            };

            var status = await _connection.OpenAsync();
            if (status == AppServiceConnectionStatus.Success)
            {
                Log($"AppService connected on attempt {attempt}");
                return;
            }

            Log($"AppService connect attempt {attempt}/{maxRetries}: {status}");
            _connection.Dispose();
            _connection = null;

            if (attempt < maxRetries)
                await Task.Delay(2000); // 2s between retries
        }

        Log("Failed to connect to AppService after all retries");
        throw new Exception($"Failed to connect to AppService after {maxRetries} attempts");
    }

    public async Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync()
    {
        var request = new ValueSet { { "command", "list_tools" } };
        var response = await _connection!.SendMessageAsync(request);

        if (response.Status != AppServiceResponseStatus.Success)
            throw new Exception($"list_tools failed: {response.Status}");

        var toolsJson = response.Message["tools"] as string ?? "[]";
        var tools = Newtonsoft.Json.JsonConvert.DeserializeObject<List<DiscoveredTool>>(toolsJson)
                    ?? new List<DiscoveredTool>();
        return tools;
    }

    public async Task<string> InvokeToolAsync(string toolName, string argumentsJson)
    {
        var request = new ValueSet
        {
            { "command", "invoke_tool" },
            { "tool", toolName },
            { "arguments", argumentsJson }
        };

        var response = await _connection!.SendMessageAsync(request);

        if (response.Status != AppServiceResponseStatus.Success)
            throw new Exception($"AppService call failed: {response.Status}");

        var status = response.Message["status"] as string;
        if (status == "success")
            return response.Message["result"] as string ?? "{}";

        var error = response.Message.ContainsKey("error")
            ? response.Message["error"] as string
            : response.Message.ContainsKey("message")
                ? response.Message["message"] as string
                : "Unknown error";
        throw new Exception($"Tool invocation failed: {error}");
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
