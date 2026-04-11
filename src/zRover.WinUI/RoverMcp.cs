using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using zRover.Core;
using zRover.Core.Logging;

namespace zRover.WinUI
{
    /// <summary>
    /// Single entry point for integrating zRover MCP into a WinUI 3 app.
    /// WinUI 3 apps are full-trust processes — no FullTrust companion needed.
    /// The MCP server runs in-process and listens on a TCP port.
    /// <para>
    /// Minimal integration (2 touch-points in App.xaml.cs):
    /// <code>
    /// // 1. In OnLaunched, after m_window.Activate():
    /// await RoverMcp.StartAsync(m_window, "MyApp");
    ///
    /// // 2. Optional: stop on window close:
    /// m_window.Closed += async (s, a) => await RoverMcp.StopAsync();
    /// </code>
    /// </para>
    /// </summary>
    public static class RoverMcp
    {
        /// <summary>
        /// Starts zRover: registers capabilities and tools, and starts the in-process
        /// MCP HTTP server.
        /// Call once from <c>OnLaunched</c> after <c>window.Activate()</c>.
        /// </summary>
        /// <param name="window">The main application window. Required for UI thread access and screenshots.</param>
        /// <param name="appName">Display name for the MCP server.</param>
        /// <param name="port">
        /// TCP port the MCP server listens on.
        /// Default is <c>5100</c> — the well-known zRover per-app port.
        /// Pass <c>0</c> to let the OS pick a free port automatically.
        /// </param>
        /// <param name="actionableApp">Optional app actions to expose via <c>list_actions</c> / <c>dispatch_action</c>.</param>
        /// <param name="managerUrl">Optional Retriever URL for session registration (e.g. <c>http://localhost:5200</c>).</param>
        public static async Task StartAsync(
            Window window,
            string appName,
            int port = RoverPorts.App,
            IActionableApp? actionableApp = null,
            string? managerUrl = null)
        {
            await DebugHost.StartAsync(window, appName, port: port, actionableApp: actionableApp, managerUrl: managerUrl);
        }

        /// <summary>
        /// Stops zRover and releases resources.
        /// </summary>
        public static async Task StopAsync()
        {
            await DebugHost.StopAsync();
        }

        // ---------------------------------------------------------------
        // Logging shorthands
        // ---------------------------------------------------------------

        /// <summary>Writes a diagnostic info message to the zRover log (visible via <c>get_logs</c>).</summary>
        public static void Log(string category, string message)
            => RoverLog.Info(category, message);

        /// <summary>Writes a warning to the zRover log.</summary>
        public static void LogWarn(string category, string message, Exception? exception = null)
            => RoverLog.Warn(category, message, exception);

        /// <summary>Writes an error to the zRover log.</summary>
        public static void LogError(string category, string message, Exception? exception = null)
            => RoverLog.Error(category, message, exception);
    }
}
