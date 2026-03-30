using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Logging;

namespace zRover.Uwp
{
    /// <summary>
    /// Entry point for the zRover in-app debug host. Active in DEBUG builds only.
    /// </summary>
    public static class DebugHost
    {
        private static DebugHostRunner? _runner;
        private static Func<Task>? _fullTrustLauncher;

        // Exposed so RoverAppService can return config to the FullTrust process
        // via the existing AppService channel (avoids any file-based IPC).
        internal static int CurrentPort { get; private set; }
        internal static string? CurrentManagerUrl { get; private set; }
        internal static string CurrentAppName { get; private set; } = "App";

        /// <summary>
        /// The in-memory log store for the running zRover instance.
        /// Backed by <see cref="RoverLog.Store"/>.
        /// MCP clients access this via the <c>get_logs</c> tool;
        /// host app code can write to it through <see cref="RoverLog"/>.
        /// </summary>
        public static IInMemoryLogStore LogStore => RoverLog.Store;
        /// <summary>
        /// Sets the callback to launch the FullTrust MCP server process.
        /// Call this before StartAsync to enable out-of-process MCP server.
        /// </summary>
        public static void SetFullTrustLauncher(Func<Task> launcher)
        {
            _fullTrustLauncher = launcher;
        }

        /// <summary>
        /// Starts the MCP debug host. Call from App.OnLaunched.
        /// </summary>
        public static async Task StartAsync(DebugHostOptions options)
        {
            // Allow idempotent calls - if already running, do nothing
            if (_runner != null)
                return;

            // Resolve free port now so CurrentPort is correct before the runner writes it.
            if (options.Port == 0)
                options.Port = FindFreePort();

            CurrentPort       = options.Port;
            CurrentManagerUrl = options.ManagerUrl;
            CurrentAppName    = options.AppName;

            _runner = new DebugHostRunner(options, _fullTrustLauncher);
            await _runner.StartAsync();
        }

        /// <summary>
        /// Convenience overload that starts the MCP debug host with common settings.
        /// Does not require a direct reference to <c>zRover.Core</c> from caller code.
        /// </summary>
        /// <param name="appName">Display name used in the MCP server info.</param>
        /// <param name="port">
        /// TCP port to listen on.
        /// Default is <c>5100</c> — the well-known zRover per-app port, so the first running
        /// instance is always reachable at a predictable address.
        /// Pass <c>0</c> to let the OS pick a free port automatically (useful when
        /// running multiple app instances simultaneously).
        /// </param>
        /// <param name="requireAuthToken">When true, callers must supply an Authorization header.</param>
        /// <param name="authToken">Bearer token to require (only used when <paramref name="requireAuthToken"/> is true).</param>
        public static Task StartAsync(
            string appName,
            int port = RoverPorts.App,
            bool requireAuthToken = false,
            string? authToken = null,
            zRover.Core.IActionableApp? actionableApp = null,
            string? managerUrl = null)
        {
            return StartAsync(new DebugHostOptions
            {
                AppName = appName,
                Port = port, // port == 0 is resolved inside StartAsync(DebugHostOptions)
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
        /// There is a small TOCTOU window — the caller must bind the real listener promptly.
        /// </summary>
        public static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>Stops the MCP debug host and releases all capabilities.</summary>
        public static async Task StopAsync()
        {
            if (_runner == null) return;
            await _runner.StopAsync();
            _runner = null;
        }
    }
}
