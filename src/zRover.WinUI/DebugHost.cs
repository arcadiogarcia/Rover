using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using zRover.Core;
using zRover.Core.Logging;

namespace zRover.WinUI
{
    /// <summary>
    /// Entry point for the zRover in-app debug host for WinUI 3 apps. Active in DEBUG builds only.
    /// </summary>
    public static class DebugHost
    {
        private static DebugHostRunner? _runner;
        private static Window? _window;

        // Exposed so callers can inspect current config.
        internal static int CurrentPort { get; private set; }
        internal static string? CurrentManagerUrl { get; private set; }
        internal static string CurrentAppName { get; private set; } = "App";
        internal static Window? CurrentWindow => _window;

        /// <summary>
        /// The in-memory log store for the running zRover instance.
        /// Backed by <see cref="RoverLog.Store"/>.
        /// MCP clients access this via the <c>get_logs</c> tool;
        /// host app code can write to it through <see cref="RoverLog"/>.
        /// </summary>
        public static IInMemoryLogStore LogStore => RoverLog.Store;

        /// <summary>
        /// Starts the MCP debug host. Call from App.OnLaunched after the window is created.
        /// </summary>
        public static async Task StartAsync(Window window, DebugHostOptions options)
        {
            if (_runner != null)
                return;

            _window = window;

            if (options.Port == 0)
                options.Port = FindFreePort();
            else if (!IsPortAvailable(options.Port))
            {
                var fallback = FindFreePort();
                RoverLog.Warn("zRover.Host", $"Port {options.Port} is already in use — falling back to {fallback}");
                System.Diagnostics.Debug.WriteLine($"[zRover] Port {options.Port} in use, falling back to {fallback}");
                options.Port = fallback;
            }

            CurrentPort       = options.Port;
            CurrentManagerUrl = options.ManagerUrl;
            CurrentAppName    = options.AppName;

            _runner = new DebugHostRunner(window, options);
            await _runner.StartAsync();
        }

        /// <summary>
        /// Convenience overload that starts the MCP debug host with common settings.
        /// </summary>
        public static Task StartAsync(
            Window window,
            string appName,
            int port = RoverPorts.App,
            bool requireAuthToken = false,
            string? authToken = null,
            IActionableApp? actionableApp = null,
            string? managerUrl = null)
        {
            return StartAsync(window, new DebugHostOptions
            {
                AppName = appName,
                Port = port,
                EnableInputInjection = true,
                EnableScreenshots = true,
                RequireAuthToken = requireAuthToken,
                AuthToken = authToken,
                ActionableApp = actionableApp,
                ManagerUrl = managerUrl
            });
        }

        /// <summary>
        /// Asks the OS for a free TCP port by binding a listener on port 0, then releasing it.
        /// </summary>
        public static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// Returns true when no process is currently listening on <paramref name="port"/>.
        /// </summary>
        private static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>Stops the MCP debug host and releases all capabilities.</summary>
        public static async Task StopAsync()
        {
            if (_runner == null) return;
            await _runner.StopAsync();
            _runner = null;
            _window = null;
        }
    }
}
