using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Coordinates;
using zRover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace zRover.WinUI.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWnd, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_MOUSEHWHEEL = 0x020E;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private void RegisterMouseTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_mouse_scroll",
                "Injects a mouse scroll event at the specified coordinates. " +
                "Moves the mouse to the target position first, then scrolls. " +
                "deltaY: negative = scroll down, positive = scroll up. " +
                "deltaX: negative = scroll left, positive = scroll right. " +
                "One wheel notch = 120 units.",
                ToolSchemas.MouseScrollSchema,
                InjectMouseScrollAsync);

            registry.RegisterTool(
                "inject_mouse_move",
                "Moves the mouse cursor to the specified coordinates without clicking. " +
                "Useful for hover effects, tooltips, or positioning before other actions.",
                ToolSchemas.MouseMoveSchema,
                InjectMouseMoveAsync);
        }

        /// <summary>
        /// Converts a DIP screen-space point to the injection coordinate expected by
        /// <see cref="InjectedInputPoint.PositionX"/>/<see cref="InjectedInputPoint.PositionY"/>
        /// for touch and pen injection (physical pixels relative to the virtual desktop origin).
        ///
        /// WinUI 3 apps are always per-monitor DPI-aware, so the resolver returns true DIP
        /// screen-space coordinates. InjectTouchInput/InjectPenInput require PHYSICAL pixel
        /// coordinates. Multiply by dpiScale (XamlRoot.RasterizationScale) to convert.
        /// </summary>
        private (int x, int y) ToTouchInjectionPoint(double dipX, double dipY, double dpiScale)
        {
            return ((int)(dipX * dpiScale), (int)(dipY * dpiScale));
        }

        // Cache the physical-to-logical scale once per process start.
        private static double? _cachedPhysicalScale;
        private static readonly object _scaleLock = new object();

        [DllImport("user32.dll")] private static extern System.IntPtr SetThreadDpiAwarenessContext(System.IntPtr dpiContext);
        private static readonly System.IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new System.IntPtr(-4);

        /// <summary>
        /// Returns the ratio of physical screen pixels to logical (DPI-unaware) pixels.
        /// On a 200% DPI display where the process is DPI-unaware, this returns 2.0.
        /// InjectTouchInput/InjectPenInput require physical pixels, so all logical DIP
        /// coordinates must be multiplied by this factor before passing to those APIs.
        /// </summary>
        private double GetPhysicalDpiScale()
        {
            if (_cachedPhysicalScale.HasValue)
                return _cachedPhysicalScale.Value;

            lock (_scaleLock)
            {
                if (_cachedPhysicalScale.HasValue)
                    return _cachedPhysicalScale.Value;

                try
                {
                    int logicalW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                    if (logicalW <= 0) { _cachedPhysicalScale = 1.0; return 1.0; }

                    // Temporarily set per-monitor-aware context to read physical metrics
                    var oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                    int physicalW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                    if (oldCtx != System.IntPtr.Zero)
                        SetThreadDpiAwarenessContext(oldCtx);

                    double scale = physicalW > 0 ? (double)physicalW / logicalW : 1.0;
                    // Clamp to reasonable range (1x – 4x)
                    if (scale < 1.0 || scale > 4.0) scale = 1.0;
                    LogToFile($"PhysicalDpiScale: logical={logicalW} physical={physicalW} scale={scale}");
                    _cachedPhysicalScale = scale;
                    return scale;
                }
                catch
                {
                    _cachedPhysicalScale = 1.0;
                    return 1.0;
                }
            }
        }

        /// <summary>
        /// Converts a DIP screen-space point to the 0–65535 range expected by
        /// <see cref="InjectedInputMouseOptions.Absolute"/> combined with
        /// <see cref="InjectedInputMouseOptions.VirtualDesk"/>, so the result is
        /// correct on the current display.
        /// </summary>
        private (int normX, int normY) ToMouseNormalized(CoordinatePoint dipPoint, double dpiScale)
        {
            // dipPoint is in DIP screen-space coordinates. MOUSEEVENTF_ABSOLUTE|VIRTUALDESK
            // normalises over PHYSICAL virtual-screen pixels: normX/65535 = physX/physVW.
            //
            // WinUI 3 apps are per-monitor DPI-aware, so GetSystemMetrics already returns
            // physical virtual-screen dimensions. Convert DIP → physical by multiplying by
            // dpiScale (XamlRoot.RasterizationScale) before normalising.
            //
            // Derivation: normX/65535 = physX/physVW = (dipX*S)/physVW.
            int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            int normX = vW > 0 ? (int)(dipPoint.X * dpiScale / vW * 65535) : 0;
            int normY = vH > 0 ? (int)(dipPoint.Y * dpiScale / vH * 65535) : 0;
            return (normX, normY);
        }

        /// <summary>
        /// PostMessages a wheel event directly to the XAML-bridge child HWNDs inside the main
        /// WinUI 3 window. WinUI 3 desktop wraps XAML content in child HWNDs. Posting
        /// WM_MOUSEWHEEL straight to those HWNDs bypasses the focus-based Win32 routing.
        /// 
        /// IMPORTANT: lParam must contain PHYSICAL pixel screen coordinates because the target
        /// HWNDs are per-monitor DPI-aware. Use GetPhysicalDpiScale() to convert logical coords.
        /// Returns true when the message was posted to at least one child.
        /// </summary>
        private bool PostScrollToXamlHwnd(IntPtr mainHwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var found = new List<IntPtr>();
            EnumChildWindows(mainHwnd, (hWnd, _) =>
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                string cls = sb.ToString();
                LogToFile($"ChildHwnd class: {cls}");
                // Target all HWNDs that are part of the WinUI 3 input/XAML stack
                if (cls.Contains("Xaml") || cls.Contains("DesktopChildSite") ||
                    cls.Contains("ContentBridge") || cls.Contains("InputSite"))
                    found.Add(hWnd);
                return true; // continue enumerating
            }, IntPtr.Zero);

            LogToFile($"PostScroll: found {found.Count} XAML child HWNDs");
            foreach (var h in found)
                PostMessage(h, msg, wParam, lParam);

            return found.Count > 0;
        }

        private async Task<string> InjectMouseScrollAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectMouseScrollRequest>(argsJson)
                      ?? new InjectMouseScrollRequest();

            LogToFile($"InjectMouseScrollAsync: ({req.X},{req.Y}) deltaY={req.DeltaY} deltaX={req.DeltaX} dryRun={req.DryRun}");

            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedTapPreview(new InjectTapRequest
                    {
                        X = req.X,
                        Y = req.Y,
                        CoordinateSpace = req.CoordinateSpace
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogToFile($"Scroll preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectMouseScrollResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    DeltaY = req.DeltaY,
                    DeltaX = req.DeltaX,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectMouseScrollResponse
                {
                    Success = false,
                    DeltaY = req.DeltaY,
                    DeltaX = req.DeltaX,
                    PreviewScreenshotPath = previewPath
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            double dpiScale2 = 1;
            Exception? error = null;

            // Resolve coordinates and move mouse on UI thread
            await _runOnUiThread(() =>
            {
                try
                {
                    ActivateWindow();
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    dpiScale2 = GetDpiScale();
                    var (normX, normY) = ToMouseNormalized(resolved, dpiScale2);

                    // Move mouse cursor to the target position.
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                     | InjectedInputMouseOptions.Absolute
                                     | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = normX,
                        DeltaY = normY
                    }});

                    LogToFile($"MouseScroll move: normX={normX} normY={normY} resolved=({resolved.X},{resolved.Y})");
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error == null)
            {
                // ── Primary: Async touch pan (WinUI 3 / pointer-based apps) ──────────────
                // WinUI 3 routes input through WM_POINTER*, not legacy WM_MOUSEWHEEL.
                // Use a slow touch pan (30 ms/step, 10 steps) to stay below the
                // ScrollViewer's inertia-fling threshold, giving proportional scrolling.
                // WHEEL_DELTA=120 per standard notch → 30 DIP pan distance per notch.
                double physScale = GetPhysicalDpiScale();
                const double dipPerNotch = 30.0;
                double scrollDipY = req.DeltaY / 120.0 * dipPerNotch;
                double scrollDipX = req.DeltaX / 120.0 * dipPerNotch;

                if (scrollDipY != 0 || scrollDipX != 0)
                {
                    const int steps = 10;
                    const int stepDelayMs = 30;
                    const int cs = 5;
                    const uint tid = 99u;
                    int startPhysX = (int)(resolved.X * physScale);
                    int startPhysY = (int)(resolved.Y * physScale);
                    int endPhysX   = (int)((resolved.X + scrollDipX) * physScale);
                    int endPhysY   = (int)((resolved.Y + scrollDipY) * physScale);

                    // PointerDown
                    await _runOnUiThread!(() =>
                    {
                        injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
                        injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                        {
                            Contact = new InjectedInputRectangle { Top = -cs, Bottom = cs, Left = -cs, Right = cs },
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = tid,
                                PointerOptions = InjectedInputPointerOptions.PointerDown
                                               | InjectedInputPointerOptions.InRange
                                               | InjectedInputPointerOptions.InContact
                                               | InjectedInputPointerOptions.New
                                               | InjectedInputPointerOptions.Primary,
                                PixelLocation = new InjectedInputPoint { PositionX = startPhysX, PositionY = startPhysY }
                            }
                        }});
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);

                    // Intermediate PointerUpdate frames with delays (controls velocity)
                    for (int i = 1; i <= steps; i++)
                    {
                        await Task.Delay(stepDelayMs).ConfigureAwait(false);
                        double f = (double)i / steps;
                        int interpX = (int)(startPhysX + (endPhysX - startPhysX) * f);
                        int interpY = (int)(startPhysY + (endPhysY - startPhysY) * f);
                        await _runOnUiThread!(() =>
                        {
                            injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                            {
                                Contact = new InjectedInputRectangle { Top = -cs, Bottom = cs, Left = -cs, Right = cs },
                                PointerInfo = new InjectedInputPointerInfo
                                {
                                    PointerId = tid,
                                    PointerOptions = InjectedInputPointerOptions.Update
                                                   | InjectedInputPointerOptions.InRange
                                                   | InjectedInputPointerOptions.InContact,
                                    PixelLocation = new InjectedInputPoint { PositionX = interpX, PositionY = interpY }
                                }
                            }});
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                    }

                    // PointerUp
                    await _runOnUiThread!(() =>
                    {
                        injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                        {
                            Contact = new InjectedInputRectangle { Top = -cs, Bottom = cs, Left = -cs, Right = cs },
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = tid,
                                PointerOptions = InjectedInputPointerOptions.PointerUp
                                               | InjectedInputPointerOptions.InRange,
                                PixelLocation = new InjectedInputPoint { PositionX = endPhysX, PositionY = endPhysY }
                            }
                        }});
                        injector.UninitializeTouchInjection();
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);

                    LogToFile($"TouchPanScroll: ({startPhysX},{startPhysY}) → ({endPhysX},{endPhysY}) steps={steps} delay={stepDelayMs}ms/step");
                }

                // ── Secondary: Mouse wheel injection (legacy Win32 apps) ───────────────────
                var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
                double physScaleW = GetPhysicalDpiScale();
                int physX = (int)(resolved.X * physScaleW);
                int physY = (int)(resolved.Y * physScaleW);
                if (req.DeltaY != 0)
                {
                    IntPtr wp = new IntPtr(unchecked((int)(((uint)(req.DeltaY & 0xFFFF)) << 16)));
                    IntPtr lp = new IntPtr(unchecked((int)(((uint)(physY & 0xFFFF)) << 16 | (uint)(physX & 0xFFFF))));
                    await _runOnUiThread!(() => { PostScrollToXamlHwnd(mainHwnd, WM_MOUSEWHEEL, wp, lp); return Task.CompletedTask; }).ConfigureAwait(false);
                }
                if (req.DeltaX != 0)
                {
                    IntPtr wp = new IntPtr(unchecked((int)(((uint)(req.DeltaX & 0xFFFF)) << 16)));
                    IntPtr lp = new IntPtr(unchecked((int)(((uint)(physY & 0xFFFF)) << 16 | (uint)(physX & 0xFFFF))));
                    await _runOnUiThread!(() => { PostScrollToXamlHwnd(mainHwnd, WM_MOUSEHWHEEL, wp, lp); return Task.CompletedTask; }).ConfigureAwait(false);
                }
            }

            if (error != null)
                LogToFile($"MouseScroll FAILED: {error.Message}");
            else
                LogToFile("MouseScroll succeeded");

            return JsonConvert.SerializeObject(new InjectMouseScrollResponse
            {
                Success = error == null,
                ResolvedCoordinates = resolved,
                DeltaY = req.DeltaY,
                DeltaX = req.DeltaX,
                PreviewScreenshotPath = previewPath
            });
        }

        private async Task<string> InjectMouseMoveAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectMouseMoveRequest>(argsJson)
                      ?? new InjectMouseMoveRequest();

            LogToFile($"InjectMouseMoveAsync: ({req.X},{req.Y}) dryRun={req.DryRun}");

            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedTapPreview(new InjectTapRequest
                    {
                        X = req.X,
                        Y = req.Y,
                        CoordinateSpace = req.CoordinateSpace
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogToFile($"MouseMove preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectMouseMoveResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectMouseMoveResponse
                {
                    Success = false,
                    PreviewScreenshotPath = previewPath
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            Exception? error = null;
            await _runOnUiThread(() =>
            {
                try
                {
                    ActivateWindow();
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    double dpiScale = GetDpiScale();
                    var (normX, normY) = ToMouseNormalized(resolved, dpiScale);

                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                     | InjectedInputMouseOptions.Absolute
                                     | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = normX,
                        DeltaY = normY
                    }});
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
                LogToFile($"MouseMove FAILED: {error.Message}");
            else
                LogToFile("MouseMove succeeded");

            return JsonConvert.SerializeObject(new InjectMouseMoveResponse
            {
                Success = error == null,
                ResolvedCoordinates = resolved,
                PreviewScreenshotPath = previewPath
            });
        }
    }
}

