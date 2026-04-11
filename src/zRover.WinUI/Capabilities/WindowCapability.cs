using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Newtonsoft.Json;
using zRover.Core;
using zRover.Core.Tools.Window;
using Windows.Graphics;

namespace zRover.WinUI.Capabilities
{
    /// <summary>
    /// Exposes window management tools — currently <c>resize_page</c>.
    /// </summary>
    internal sealed class WindowCapability : IDebugCapability
    {
        private DebugHostContext? _context;
        private readonly Window _window;

        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hWnd);

        public string Name => "Window";

        public WindowCapability(Window window)
        {
            _window = window;
        }

        public Task StartAsync(DebugHostContext context)
        {
            _context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _context = null;
            return Task.CompletedTask;
        }

        private const string ResizeSchema = @"{
  ""type"": ""object"",
  ""required"": [""width"", ""height""],
  ""properties"": {
    ""width"": { ""type"": ""integer"", ""description"": ""Requested window width in DIPs (device-independent pixels)."" },
    ""height"": { ""type"": ""integer"", ""description"": ""Requested window height in DIPs (device-independent pixels)."" }
  }
}";

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "resize_page",
                "Requests a resize of the WinUI 3 app window to the specified width and height in DIPs " +
                "(device-independent pixels — the same unit as XAML layout). " +
                "The OS may adjust the final size to enforce minimum dimensions or display constraints. " +
                "Returns the actual window client size after the resize attempt. " +
                "Useful for testing adaptive/responsive layouts and VisualStateManager breakpoints.",
                ResizeSchema,
                ResizeAsync);
        }

        private async Task<string> ResizeAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<ResizeWindowRequest>(argsJson)
                          ?? new ResizeWindowRequest();

                double actualWidth = 0, actualHeight = 0;

                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(() =>
                    {
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = AppWindow.GetFromWindowId(windowId);

                        // Convert DIPs to physical pixels using DPI
                        int dpi = GetDpiForWindow(hwnd);
                        double scale = dpi / 96.0;
                        int physW = (int)Math.Round(req.Width * scale);
                        int physH = (int)Math.Round(req.Height * scale);

                        appWindow.Resize(new SizeInt32(physW, physH));

                        // Return actual client size in DIPs
                        var clientSize = appWindow.ClientSize;
                        actualWidth  = clientSize.Width / scale;
                        actualHeight = clientSize.Height / scale;

                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                return JsonConvert.SerializeObject(new ResizeWindowResponse
                {
                    Success = true,
                    ActualWidth = (int)actualWidth,
                    ActualHeight = (int)actualHeight
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }
    }
}
