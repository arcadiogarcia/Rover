using System;
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
        /// In DPI-unaware processes, Win32 coordinates are logical (not physical). However,
        /// InjectTouchInput/InjectPenInput always require PHYSICAL pixel coordinates. To
        /// bridge the gap, we temporarily switch to per-monitor DPI-aware context to get the
        /// physical virtual-screen size, compute the physical:logical ratio, then restore.
        /// </summary>
        private (int x, int y) ToTouchInjectionPoint(double dipX, double dipY, double dpiScale)
        {
            double physicalScale = GetPhysicalDpiScale();
            return ((int)(dipX * physicalScale), (int)(dipY * physicalScale));
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
            // Convert DIP → raw physical pixels.
            double rawX = dipPoint.X * dpiScale;
            double rawY = dipPoint.Y * dpiScale;

            // Use Win32 GetSystemMetrics to get virtual screen dimensions in raw pixels.
            int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            int normX = vW > 0 ? (int)(rawX / vW * 65535) : 0;
            int normY = vH > 0 ? (int)(rawY / vH * 65535) : 0;
            return (normX, normY);
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

                    // Move mouse to position (VirtualDesk ensures correct placement on any monitor)
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                     | InjectedInputMouseOptions.Absolute
                                     | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = normX,
                        DeltaY = normY
                    }});

                    // Vertical scroll
                    if (req.DeltaY != 0)
                    {
                        injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                        {
                            MouseOptions = InjectedInputMouseOptions.Wheel,
                            MouseData = (uint)req.DeltaY
                        }});
                    }

                    // Horizontal scroll
                    if (req.DeltaX != 0)
                    {
                        injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                        {
                            MouseOptions = InjectedInputMouseOptions.HWheel,
                            MouseData = (uint)req.DeltaX
                        }});
                    }
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

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

