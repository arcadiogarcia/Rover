using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using zRover.Core;
using zRover.Core.Logging;
using zRover.Mcp;
using zRover.WinUI.Capabilities;
using zRover.WinUI.Coordinates;

namespace zRover.WinUI
{
    internal class DebugHostRunner
    {
        private readonly Window _window;
        private readonly DebugHostOptions _options;
        private readonly List<IDebugCapability> _capabilities = new List<IDebugCapability>();
        private CancellationTokenSource? _serverCts;

        public DebugHostRunner(Window window, DebugHostOptions options)
        {
            _window = window;
            _options = options;
        }

        public async Task StartAsync()
        {
            if (_options.EnableLogging && _options.LogBufferCapacity != 2000)
                RoverLog.Store = new InMemoryLogStore(_options.LogBufferCapacity);

            RoverLog.Info("zRover.Host", $"Starting '{_options.AppName}' MCP debug host (WinUI 3)");

            WireWinUIDiagnostics();

            var dispatcherQueue = _window.DispatcherQueue;
            Func<Func<Task>, Task> runOnUiThread = async (work) =>
            {
                var tcs = new TaskCompletionSource<bool>();
                dispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await work();
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                await tcs.Task;
            };

            var resolver = new WinUICoordinateResolver(_window);
            var artifactDir = _options.ArtifactDirectory
                ?? GetArtifactDirectory(_options.AppName);

            var context = new DebugHostContext(_options, resolver, artifactDir, runOnUiThread, RoverLog.Store);

            Directory.CreateDirectory(Path.Combine(artifactDir, "screenshots"));
            Directory.CreateDirectory(Path.Combine(artifactDir, "logs"));

            if (_options.EnableLogging)
                _capabilities.Add(new zRover.WinUI.Capabilities.LoggingCapability());
            if (_options.EnableInputInjection)
                _capabilities.Add(new InputInjectionCapability(_window));
            if (_options.EnableScreenshots)
                _capabilities.Add(new ScreenshotCapability(_window));
            if (_options.EnableFrameCapture)
                _capabilities.Add(new FrameCaptureCapability(_window));
            if (_options.ActionableApp != null)
                _capabilities.Add(new AppActionCapability(_options.ActionableApp));
            if (_options.EnableUiTree)
                _capabilities.Add(new UiTreeCapability(_window));
            if (_options.EnableWindowManagement)
                _capabilities.Add(new WindowCapability(_window));
            if (_options.EnableWaitFor)
                _capabilities.Add(new WaitForCapability());

            foreach (var capability in _capabilities)
            {
                await capability.StartAsync(context).ConfigureAwait(false);
                RoverLog.Info("zRover.Host", $"Capability '{capability.Name}' started");
            }

            foreach (var capability in _capabilities)
            {
                if (capability is InputInjectionCapability inputCapability && !inputCapability.InjectorAvailable)
                {
                    var error = inputCapability.InjectorError ?? "InputInjector.TryCreate() returned null";
                    RoverLog.Warn("zRover.InputInjection", $"Input injection unavailable — {error}");
                    System.Diagnostics.Debug.WriteLine($"[zRover] WARNING: Input injection unavailable — {error}");
                }
            }

            var adapter = new McpToolRegistryAdapter();
            foreach (var capability in _capabilities)
                capability.RegisterTools(adapter);

            RoverLog.Info("zRover.Host", $"Registered {adapter.Tools.Count} tools");
            System.Diagnostics.Debug.WriteLine($"[zRover] Registered {adapter.Tools.Count} tools");

            // Start the MCP HTTP server in the background
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;

            // Register with Retriever if configured
            if (_options.ManagerUrl != null)
            {
                var mcpUrl = $"http://localhost:{_options.Port}/mcp";
                _ = Task.Run(() => RegisterWithManagerAsync(_options.ManagerUrl, _options.AppName, mcpUrl, token));
            }

            _ = Task.Run(() => RunMcpServerAsync(adapter, _options.Port, _options.AuthToken, token));

            RoverLog.Info("zRover.Host", $"'{_options.AppName}' MCP debug host started — port {_options.Port}");
            System.Diagnostics.Debug.WriteLine($"[zRover] '{_options.AppName}' MCP debug host started on port {_options.Port}");
        }

        public async Task StopAsync()
        {
            RoverLog.Info("zRover.Host", "Debug host stopping");
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _serverCts = null;

            foreach (var capability in _capabilities)
                await capability.StopAsync().ConfigureAwait(false);
            _capabilities.Clear();

            RoverLog.Info("zRover.Host", "Debug host stopped");
            System.Diagnostics.Debug.WriteLine("[zRover] Debug host stopped.");
        }

        private static async Task RunMcpServerAsync(
            McpToolRegistryAdapter adapter,
            int port,
            string? authToken,
            CancellationToken cancellationToken)
        {
            try
            {
                var builder = WebApplication.CreateBuilder(new string[] { "--urls", $"http://localhost:{port}" });

                foreach (var tool in adapter.Tools)
                    builder.Services.AddSingleton<McpServerTool>(tool);

                builder.Services.AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation { Name = "zRover", Version = "1.0.0" };
                    options.Capabilities = new ServerCapabilities
                    {
                        Tools = new ToolsCapability { ListChanged = true }
                    };
                    options.ToolCollection = adapter.Tools;
                }).WithHttpTransport();

                var app = builder.Build();

                if (!string.IsNullOrEmpty(authToken))
                {
                    app.Use(async (ctx, next) =>
                    {
                        var auth = ctx.Request.Headers["Authorization"].ToString();
                        if (auth != $"Bearer {authToken}")
                        {
                            ctx.Response.StatusCode = 401;
                            ctx.Response.ContentType = "text/plain";
                            var bytes = System.Text.Encoding.UTF8.GetBytes("Unauthorized");
                            await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                            return;
                        }
                        await next(ctx);
                    });
                }

                app.MapMcp("/mcp");
                cancellationToken.Register(() => app.Lifetime.StopApplication());
                System.Diagnostics.Debug.WriteLine($"[zRover] MCP server running on http://localhost:{port}/mcp");
                await app.RunAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine("[zRover] MCP server stopped.");
            }
            catch (Exception ex)
            {
                RoverLog.Error("zRover.Host", $"MCP server error: {ex.Message}", ex);
                System.Diagnostics.Debug.WriteLine($"[zRover] MCP server error: {ex}");
            }
        }

        private static async Task RegisterWithManagerAsync(
            string managerUrl,
            string appName,
            string mcpUrl,
            CancellationToken cancellationToken)
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = managerUrl.TrimEnd('/') + "/sessions/register";
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                appName,
                version = "1.0",
                mcpUrl
            });

            const int maxAttempts = 8;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(url, content, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    RoverLog.Info("zRover.Host", $"Registered with Retriever at {managerUrl}");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    RoverLog.Warn("zRover.Host", $"Manager registration attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 30)), cancellationToken);
                }
            }
            RoverLog.Warn("zRover.Host", "Could not reach Retriever after all attempts — running standalone.");
        }

        private static string GetArtifactDirectory(string appName)
        {
            try
            {
                // Prefer the app's local data folder (works for packaged apps)
                return Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "debug-artifacts");
            }
            catch
            {
                // Fallback for unpackaged apps
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "zRover", appName, "debug-artifacts");
            }
        }

        private static bool _diagnosticsWired;

        private static void WireWinUIDiagnostics()
        {
            if (_diagnosticsWired) return;
            _diagnosticsWired = true;

            try
            {
                Microsoft.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
                {
                    RoverLog.Fatal("App.UnhandledException", e.Message, e.Exception);
                    System.Diagnostics.Debug.WriteLine($"[zRover] FATAL App.UnhandledException: {e.Message}");
                };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    RoverLog.Error("TaskScheduler.UnobservedTaskException",
                        e.Exception?.Message ?? "Unobserved task exception", e.Exception);
                    e.SetObserved();
                };

                RoverLog.Info("zRover.Host", "WinUI 3 diagnostics wired (crash handlers)");

#if DEBUG
                Microsoft.UI.Xaml.Application.Current.DebugSettings.IsBindingTracingEnabled = true;
                Microsoft.UI.Xaml.Application.Current.DebugSettings.BindingFailed += (s, e) =>
                    RoverLog.Warn("XAML.BindingFailed", e.Message);
                RoverLog.Debug("zRover.Host", "XAML binding failure tracing enabled");
#endif
            }
            catch (Exception ex)
            {
                RoverLog.Warn("zRover.Host", $"Could not wire some WinUI 3 diagnostics: {ex.Message}", ex);
            }
        }
    }
}
