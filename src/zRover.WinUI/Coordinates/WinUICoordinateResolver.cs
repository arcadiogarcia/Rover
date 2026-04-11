using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using zRover.Core.Coordinates;

namespace zRover.WinUI.Coordinates
{
    internal sealed class WinUICoordinateResolver : ICoordinateResolver
    {
        private readonly Window _window;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT_NATIVE { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT_NATIVE { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")] private static extern bool ClientToScreen(System.IntPtr hWnd, ref POINT_NATIVE lpPoint);
        [DllImport("user32.dll")] private static extern bool GetClientRect(System.IntPtr hWnd, out RECT_NATIVE lpRect);
        [DllImport("user32.dll")] private static extern System.IntPtr GetWindow(System.IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern System.IntPtr GetParent(System.IntPtr hWnd);

        // GetWindow commands
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;

        public WinUICoordinateResolver(Window window)
        {
            _window = window;
        }

        /// <summary>
        /// Finds the innermost child HWND of the outer WinUI 3 window that fully covers
        /// the outer window's client area. In WinUI 3, the XAML rendering happens in a
        /// nested child HWND (the XAML island bridge); ClientToScreen on that inner HWND
        /// gives the correct screen origin for the XAML coordinate system.
        /// </summary>
        private System.IntPtr FindXamlIslandHwnd(System.IntPtr outerHwnd)
        {
            // Get the outer client rect to know what size we're looking for.
            if (!GetClientRect(outerHwnd, out var outerRect))
                return outerHwnd;
            int outerW = outerRect.Right - outerRect.Left;
            int outerH = outerRect.Bottom - outerRect.Top;
            if (outerW <= 0 || outerH <= 0)
                return outerHwnd;

            return FindDeepestMatchingChild(outerHwnd, outerW, outerH);
        }

        private System.IntPtr FindDeepestMatchingChild(System.IntPtr hwnd, int targetW, int targetH)
        {
            var deepest = hwnd;
            var child = GetWindow(hwnd, GW_CHILD);
            while (child != System.IntPtr.Zero)
            {
                if (GetClientRect(child, out var childRect))
                {
                    int cw = childRect.Right - childRect.Left;
                    int ch = childRect.Bottom - childRect.Top;
                    if (System.Math.Abs(cw - targetW) <= 4 && System.Math.Abs(ch - targetH) <= 4)
                    {
                        // This child matches; go deeper to find the real XAML leaf HWND
                        var deeper = FindDeepestMatchingChild(child, targetW, targetH);
                        if (deeper != child)
                            deepest = deeper;
                        else
                            deepest = child;
                        break; // Take the first matching sibling
                    }
                }
                child = GetWindow(child, GW_HWNDNEXT);
            }
            return deepest;
        }

        /// <summary>
        /// Resolves caller-supplied coordinates into screen-absolute DIP coordinates
        /// suitable for InputInjector APIs (absolute mouse / touch / pen injection).
        ///
        /// Normalized (default): 0..1 maps to the content ActualWidth/ActualHeight.
        /// Pixels: render-pixel coordinates (same unit as bitmapWidth/bitmapHeight from
        ///   capture_current_view). Internally divided by rasterizationScale to get DIPs.
        ///
        /// In both cases the window's client-area screen origin is added so the result
        /// is screen-absolute, matching what UwpCoordinateResolver returns via
        /// CoreWindow.Bounds.
        /// </summary>
        private System.IO.StreamWriter? _log;
        private System.IO.StreamWriter GetLog()
        {
            if (_log == null)
            {
                var dir = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "debug-artifacts");
                System.IO.Directory.CreateDirectory(dir);
                _log = new System.IO.StreamWriter(System.IO.Path.Combine(dir, "coord-resolver.log"), append: true) { AutoFlush = true };
            }
            return _log;
        }

        public CoordinatePoint Resolve(CoordinatePoint point, CoordinateSpace space)
        {
            var content = _window.Content as FrameworkElement;

            double dipX, dipY;
            if (space == CoordinateSpace.Pixels)
            {
                double scale = GetRasterizationScale();
                dipX = scale > 0 ? point.X / scale : point.X;
                dipY = scale > 0 ? point.Y / scale : point.Y;
            }
            else // Normalized
            {
                double w = content?.ActualWidth ?? 0;
                double h = content?.ActualHeight ?? 0;

                if (w == 0 || h == 0)
                {
                    // Fallback: use AppWindow client size
                    try
                    {
                        var appWindow = GetAppWindow();
                        if (appWindow != null)
                        {
                            if (w == 0) w = appWindow.ClientSize.Width;
                            if (h == 0) h = appWindow.ClientSize.Height;
                            double scale = GetRasterizationScale();
                            if (scale > 0) { w /= scale; h /= scale; }
                        }
                    }
                    catch { /* best-effort */ }
                }

                dipX = point.X * w;
                dipY = point.Y * h;
            }

            // Add the window's client-area screen origin so the result is screen-absolute.
            // In WinUI 3, the XAML content is hosted in a child HWND (XAML island bridge);
            // ClientToScreen on the outermost matching child gives the correct screen origin.
            try
            {
                var outerHwnd = GetWindowHandle();
                var contentHwnd = FindXamlIslandHwnd(outerHwnd);
                var pt = new POINT_NATIVE { X = 0, Y = 0 };
                if (ClientToScreen(contentHwnd, ref pt))
                {
                    double scale = GetRasterizationScale();
                    // Also log AppWindow position for diagnostic comparison
                    var appWin = GetAppWindow();
                    int dpiWin = GetDpiForWindow(contentHwnd);
                    string appWinStr = appWin != null
                        ? $"appWin.Pos=({appWin.Position.X},{appWin.Position.Y}) ClientSize=({appWin.ClientSize.Width}x{appWin.ClientSize.Height}) dpiForWin={dpiWin}"
                        : $"appWin=null dpiForWin={dpiWin}";
                    GetLog().WriteLine($"[Resolve] space={space} in=({point.X},{point.Y}) contentDIP=({dipX:F1},{dipY:F1}) clientPx=({pt.X},{pt.Y}) outerHwnd={outerHwnd:X} contentHwnd={contentHwnd:X} scale={scale} {appWinStr} out=({dipX + pt.X/scale:F1},{dipY + pt.Y/scale:F1})");
                    dipX += pt.X / scale;
                    dipY += pt.Y / scale;
                }
            }
            catch { /* best-effort: return window-relative coords on failure */ }

            return new CoordinatePoint(dipX, dipY);
        }

        internal double GetRasterizationScale()
        {
            if (_window.Content?.XamlRoot?.RasterizationScale is double scale && scale > 0)
                return scale;
            // Fallback: use Win32 DPI
            try
            {
                var hwnd = GetWindowHandle();
                int dpi = GetDpiForWindow(hwnd);
                return dpi / 96.0;
            }
            catch
            {
                return 1.0;
            }
        }

        internal Microsoft.UI.Windowing.AppWindow? GetAppWindow()
        {
            try
            {
                var hwnd = GetWindowHandle();
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            }
            catch { return null; }
        }

        internal System.IntPtr GetWindowHandle()
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(_window);
        }

        [DllImport("user32.dll")] private static extern int GetDpiForWindow(System.IntPtr hwnd);
    }
}
