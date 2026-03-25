using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Coordinates;
using Rover.Core.Tools.InputInjection;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;
using Windows.UI.Xaml;

namespace Rover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability : IDebugCapability
    {
        private InputInjector? _injector;
        private ICoordinateResolver? _resolver;
        private Func<Func<Task>, Task>? _runOnUiThread;
        private string? _logDir;
        private string? _injectorError;

        private const string InjectorUnavailableMessage =
            "InputInjector is not available. Ensure the 'inputInjectionBrokered' restricted capability " +
            "is declared in Package.appxmanifest and the app is running with the required permissions.";

        /// <summary>
        /// True if the InputInjector API was successfully created during startup.
        /// </summary>
        public bool InjectorAvailable => _injector != null;

        /// <summary>
        /// If <see cref="InjectorAvailable"/> is false, contains the reason.
        /// </summary>
        public string? InjectorError => _injectorError;

        /// <summary>
        /// Returns a JSON error response with the standard unavailable message.
        /// The JSON includes "success": false and "error": "...".
        /// </summary>
        private string InjectorUnavailableResponse(object responseObj)
        {
            var json = JsonConvert.SerializeObject(responseObj);
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            obj["error"] = _injectorError ?? InjectorUnavailableMessage;
            return obj.ToString(Formatting.None);
        }

        private void LogToFile(string message)
        {
            try
            {
                if (_logDir == null) return;
                var path = System.IO.Path.Combine(_logDir, "input-injection.log");
                System.IO.File.AppendAllText(path,
                    $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
        }

        private const string TapSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate. In the default normalized space this is 0.0 (left) to 1.0 (right)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate. In the default normalized space this is 0.0 (top) to 1.0 (bottom)."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""absolute"", ""normalized"", ""client""], ""default"": ""normalized"", ""description"": ""Coordinate space: 'normalized' (default, 0-1 relative to app window), 'client' (window pixels), or 'absolute' (screen pixels)."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, captures an annotated screenshot showing where the tap would land but does NOT actually inject the input. Use this to verify coordinates before committing."" }
  },
  ""required"": [""x"", ""y""]
}";

        private const string DragSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""points"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/$defs/point"" }, ""minItems"": 2, ""description"": ""Ordered waypoints for the drag gesture."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 300, ""description"": ""Total duration of the drag in milliseconds."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""absolute"", ""normalized"", ""client""], ""default"": ""normalized"", ""description"": ""Coordinate space: 'normalized' (default, 0-1 relative to app window), 'client' (window pixels), or 'absolute' (screen pixels)."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""description"": ""If true, captures an annotated screenshot showing the drag path but does NOT actually inject the input. Use this to verify the path before committing."" }
  },
  ""required"": [""points""],
  ""$defs"": { ""point"": { ""type"": ""object"", ""properties"": { ""x"": {""type"":""number"", ""description"": ""X position (0.0–1.0 in normalized space)""}, ""y"": {""type"":""number"", ""description"": ""Y position (0.0–1.0 in normalized space)""} }, ""required"": [""x"",""y""] } }
}";

        public string Name => "InputInjection";

        public async Task StartAsync(DebugHostContext context)
        {
            _resolver = context.CoordinateResolver;
            _runOnUiThread = context.RunOnUiThread;
            _logDir = context.ArtifactDirectory;

            // InputInjector must be created on the UI thread.
            if (context.RunOnUiThread != null)
            {
                await context.RunOnUiThread(() =>
                {
                    TryCreateInjector();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            else
            {
                TryCreateInjector();
            }
        }

        private void TryCreateInjector()
        {
            try
            {
                _injector = InputInjector.TryCreate();
                if (_injector != null)
                {
                    LogToFile("InputInjector created");
                    _injectorError = null;
                }
                else
                {
                    _injectorError = InjectorUnavailableMessage;
                    LogToFile("InputInjector.TryCreate() returned null — " + _injectorError);
                }
            }
            catch (Exception ex)
            {
                _injectorError = $"InputInjector.TryCreate() threw {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8}). " +
                                 "Ensure the 'inputInjectionBrokered' restricted capability is declared in Package.appxmanifest.";
                LogToFile($"InputInjector.TryCreate() FAILED: {ex.GetType().FullName}: {ex.Message}");
                LogToFile($"HRESULT: 0x{ex.HResult:X8}");
                LogToFile($"StackTrace: {ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine($"[InputInjection] Error creating InputInjector: {ex}");
                _injector = null;
            }
        }

        public Task StopAsync()
        {
            _injector?.UninitializeTouchInjection();
            _injector = null;
            return Task.CompletedTask;
        }

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_tap",
                "Injects a tap/touch event at the specified coordinates. " +
                "Returns an annotated screenshot showing the tap location BEFORE the input is injected, " +
                "so you can verify the target. The crosshair marker is drawn with contrasting colors for visibility. " +
                "Set dryRun=true to preview the tap location without actually injecting input. " +
                "Coordinates default to normalized space (0.0–1.0 relative to the app window), where (0,0) is the top-left corner and (1,1) is the bottom-right. " +
                "Use capture_current_view first to see the UI layout.",
                TapSchema,
                InjectTapAsync);

            registry.RegisterTool(
                "inject_drag_path",
                "Injects a drag gesture along a path of points. " +
                "Returns an annotated screenshot showing the drag path BEFORE the input is injected, " +
                "with the start point (green circle), path (cyan line), waypoints (yellow dots), and end point (red diamond) visualized. " +
                "Set dryRun=true to preview the drag path without actually injecting input. " +
                "Coordinates default to normalized space (0.0–1.0 relative to the app window), where (0,0) is the top-left corner and (1,1) is the bottom-right. " +
                "Use capture_current_view first to see the UI layout.",
                DragSchema,
                InjectDragPathAsync);

            RegisterKeyboardTools(registry);
            RegisterMouseTools(registry);
            RegisterTouchTools(registry);
            RegisterPenTools(registry);
            RegisterGamepadTools(registry);
        }

        private async Task<string> InjectTapAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectTapRequest>(argsJson)
                      ?? new InjectTapRequest();

            LogToFile($"InjectTapAsync: ({req.X},{req.Y}) space={req.CoordinateSpace} device={req.Device} dryRun={req.DryRun}");

            // --- Capture and annotate a preview screenshot BEFORE injection ---
            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedTapPreview(req).ConfigureAwait(false);
                    LogToFile($"Preview screenshot: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"Preview screenshot failed: {ex.Message}");
                }
            }

            // If dry run, return preview without injecting
            if (req.DryRun)
            {
                var dryResponse = new InjectTapResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    Device = req.Device,
                    DryRun = true,
                    PreviewScreenshotPath = previewPath
                };
                return JsonConvert.SerializeObject(dryResponse);
            }

            // --- Proceed with actual injection ---
            var injector = _injector;
            if (injector != null && _runOnUiThread != null)
            {
                try
                {
                    string? result = null;
                    Exception? innerEx = null;
                    await _runOnUiThread(() =>
                    {
                        try
                        {
                            result = InjectTapViaInjector(injector, req);
                        }
                        catch (Exception ex)
                        {
                            innerEx = ex;
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);

                    if (result != null)
                    {
                        LogToFile($"InputInjector tap succeeded");
                        return AttachPreviewPath(result, previewPath);
                    }
                    if (innerEx != null)
                    {
                        LogToFile($"InputInjector tap FAILED: {innerEx.GetType().FullName}: {innerEx.Message} HRESULT=0x{innerEx.HResult:X8}");
                    }
                }
                catch (Exception ex)
                {
                    LogToFile($"InputInjector tap outer FAILED: {ex.GetType().FullName}: {ex.Message}");
                }
            }

            LogToFile("InputInjector is not available. Ensure the inputInjectionBrokered capability is declared.");
            var errorResponse = new InjectTapResponse
            {
                Success = false,
                Device = req.Device,
                ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                PreviewScreenshotPath = previewPath,
                Timestamp = DateTimeOffset.UtcNow.ToString("o")
            };
            return InjectorUnavailableResponse(errorResponse);
        }

        private string InjectTapViaInjector(InputInjector injector, InjectTapRequest req)
        {
            try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { }

            var space = ParseSpace(req.CoordinateSpace);
            var resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);

            var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
            double dpiScale = dispInfo.RawPixelsPerViewPixel;
            double rawX = resolved.X * dpiScale;
            double rawY = resolved.Y * dpiScale;

            LogToFile($"Tap DIP=({resolved.X:F1},{resolved.Y:F1}) RAW=({rawX:F0},{rawY:F0}) dpi={dpiScale}");

            var useTouch = !string.Equals(req.Device, "mouse", StringComparison.OrdinalIgnoreCase);

            if (!useTouch)
            {
                // Mouse: 0-65535 normalized virtual screen coords
                double rawW = dispInfo.ScreenWidthInRawPixels;
                double rawH = dispInfo.ScreenHeightInRawPixels;
                double screenDipW = rawW / dpiScale;
                double screenDipH = rawH / dpiScale;

                int normX = (int)(resolved.X / screenDipW * 65535);
                int normY = (int)(resolved.Y / screenDipH * 65535);

                injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                {
                    MouseOptions = InjectedInputMouseOptions.Move | InjectedInputMouseOptions.Absolute,
                    DeltaX = normX, DeltaY = normY
                }});
                System.Threading.Thread.Sleep(50);
                injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                {
                    MouseOptions = InjectedInputMouseOptions.LeftDown
                }});
                System.Threading.Thread.Sleep(50);
                injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                {
                    MouseOptions = InjectedInputMouseOptions.LeftUp
                }});
            }
            else
            {
                // InjectedInputPoint.PositionX/Y expects RAW SCREEN PIXELS, not DIPs
                int touchX = (int)rawX;
                int touchY = (int)rawY;
                LogToFile($"Touch injection at raw pixels ({touchX},{touchY})");

                injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);

                var info = new InjectedInputTouchInfo
                {
                    Contact = new InjectedInputRectangle { Top = -1, Bottom = 1, Left = -1, Right = 1 },
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = 1,
                        PointerOptions = InjectedInputPointerOptions.PointerDown
                                       | InjectedInputPointerOptions.InContact
                                       | InjectedInputPointerOptions.New,
                        PixelLocation = new InjectedInputPoint
                        {
                            PositionX = touchX,
                            PositionY = touchY
                        },
                    },
                    Pressure = 1.0,
                    TouchParameters = InjectedInputTouchParameters.Pressure
                                    | InjectedInputTouchParameters.Contact
                };

                injector.InjectTouchInput(new[] { info });

                var liftInfo = new InjectedInputTouchInfo
                {
                    Contact = new InjectedInputRectangle { Top = -1, Bottom = 1, Left = -1, Right = 1 },
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = 1,
                        PointerOptions = InjectedInputPointerOptions.PointerUp,
                        PixelLocation = new InjectedInputPoint
                        {
                            PositionX = touchX,
                            PositionY = touchY
                        },
                    },
                    Pressure = 0.0,
                    TouchParameters = InjectedInputTouchParameters.Pressure
                                    | InjectedInputTouchParameters.Contact
                };
                injector.InjectTouchInput(new[] { liftInfo });

                injector.UninitializeTouchInjection();
            }

            var response = new InjectTapResponse
            {
                Success = true,
                ResolvedCoordinates = resolved,
                Device = req.Device
            };
            return JsonConvert.SerializeObject(response);
        }

        private async Task<string> InjectDragPathAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectDragPathRequest>(argsJson)
                      ?? new InjectDragPathRequest();

            LogToFile($"InjectDragPathAsync called: {req.Points?.Count ?? 0} points, device={req.Device} dryRun={req.DryRun}");

            // --- Capture and annotate a preview screenshot BEFORE injection ---
            string? previewPath = null;
            if (_runOnUiThread != null)
            {
                try
                {
                    previewPath = await CaptureAnnotatedDragPreview(req).ConfigureAwait(false);
                    LogToFile($"Preview screenshot: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"Preview screenshot failed: {ex.Message}");
                }
            }

            // If dry run, return preview without injecting
            if (req.DryRun)
            {
                var resolvedPath = new List<CoordinatePoint>();
                foreach (var pt in req.Points)
                    resolvedPath.Add(new CoordinatePoint(pt.X, pt.Y));

                var dryResponse = new InjectDragPathResponse
                {
                    Success = true,
                    PointCount = req.Points.Count,
                    DurationMs = req.DurationMs,
                    ResolvedPath = resolvedPath,
                    Device = req.Device,
                    DryRun = true,
                    PreviewScreenshotPath = previewPath
                };
                return JsonConvert.SerializeObject(dryResponse);
            }

            // --- Proceed with actual injection ---
            var injector = _injector;
            if (injector != null && _runOnUiThread != null)
            {
                LogToFile("Using InputInjector drag path (async)");
                try
                {
                    var result = await InjectDragViaInjectorAsync(injector, req).ConfigureAwait(false);
                    if (result != null)
                    {
                        LogToFile("InputInjector drag succeeded");
                        return AttachPreviewPath(result, previewPath);
                    }
                }
                catch (Exception ex)
                {
                    LogToFile($"InputInjector drag FAILED: {ex.GetType().FullName}: {ex.Message} HRESULT=0x{ex.HResult:X8}");
                }
            }

            LogToFile("InputInjector is not available. Ensure the inputInjectionBrokered capability is declared.");
            var errorResponse = new InjectDragPathResponse
            {
                Success = false,
                Device = req.Device,
                DurationMs = req.DurationMs,
                PointCount = 0,
                ResolvedPath = new List<CoordinatePoint>(),
                PreviewScreenshotPath = previewPath
            };
            return InjectorUnavailableResponse(errorResponse);
        }

        private async Task<string?> InjectDragViaInjectorAsync(InputInjector injector, InjectDragPathRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);
            var resolvedPath = new List<CoordinatePoint>();
            double dpiScale = 0;
            var useTouch = !string.Equals(req.Device, "mouse", StringComparison.OrdinalIgnoreCase);

            // Resolve coordinates and get DPI info on UI thread
            await _runOnUiThread!(() =>
            {
                try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { }
                var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                dpiScale = dispInfo.RawPixelsPerViewPixel;
                foreach (var pt in req.Points)
                    resolvedPath.Add(_resolver!.Resolve(pt, space));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            LogToFile($"Drag resolved DIP path: {string.Join(" -> ", resolvedPath.Select(p => $"({p.X:F0},{p.Y:F0})"))} dpi={dpiScale}");

            if (resolvedPath.Count < 2 || dpiScale <= 0)
                return null;

            // Generate intermediate points (DIP coordinates) for smooth drag
            var allPoints = new List<CoordinatePoint>();
            for (int i = 0; i < resolvedPath.Count - 1; i++)
            {
                int intermediateSteps = Math.Max(10, req.DurationMs / 16);
                if (resolvedPath.Count > 2) intermediateSteps = Math.Max(5, intermediateSteps / (resolvedPath.Count - 1));
                for (int j = 0; j <= intermediateSteps; j++)
                {
                    double t = (double)j / intermediateSteps;
                    double x = resolvedPath[i].X + t * (resolvedPath[i + 1].X - resolvedPath[i].X);
                    double y = resolvedPath[i].Y + t * (resolvedPath[i + 1].Y - resolvedPath[i].Y);
                    allPoints.Add(new CoordinatePoint(x, y));
                }
            }

            int totalDelayMs = req.DurationMs;
            int delayPerPoint = Math.Max(1, totalDelayMs / Math.Max(1, allPoints.Count - 1));

            if (useTouch)
            {
                // Touch drag: dispatch each event on UI thread with async waits between,
                // allowing the UI thread to process each touch event as it arrives.
                int startRawX = (int)(allPoints[0].X * dpiScale);
                int startRawY = (int)(allPoints[0].Y * dpiScale);
                LogToFile($"Touch drag start at raw ({startRawX},{startRawY})");

                await _runOnUiThread!(() =>
                {
                    injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
                    injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                    {
                        Contact = new InjectedInputRectangle { Top = -4, Bottom = 4, Left = -4, Right = 4 },
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = InjectedInputPointerOptions.PointerDown
                                           | InjectedInputPointerOptions.InContact
                                           | InjectedInputPointerOptions.New,
                            PixelLocation = new InjectedInputPoint { PositionX = startRawX, PositionY = startRawY }
                        },
                        Pressure = 1.0,
                        TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                    }});
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                for (int i = 1; i < allPoints.Count; i++)
                {
                    await Task.Delay(delayPerPoint).ConfigureAwait(false);
                    int rawX = (int)(allPoints[i].X * dpiScale);
                    int rawY = (int)(allPoints[i].Y * dpiScale);
                    await _runOnUiThread!(() =>
                    {
                        injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                        {
                            Contact = new InjectedInputRectangle { Top = -4, Bottom = 4, Left = -4, Right = 4 },
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = 1,
                                PointerOptions = InjectedInputPointerOptions.InContact,
                                PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                            },
                            Pressure = 1.0,
                            TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                        }});
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                int endRawX = (int)(allPoints[allPoints.Count - 1].X * dpiScale);
                int endRawY = (int)(allPoints[allPoints.Count - 1].Y * dpiScale);
                LogToFile($"Touch drag end at raw ({endRawX},{endRawY})");

                await _runOnUiThread!(() =>
                {
                    injector.InjectTouchInput(new[] { new InjectedInputTouchInfo
                    {
                        Contact = new InjectedInputRectangle { Top = -4, Bottom = 4, Left = -4, Right = 4 },
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = InjectedInputPointerOptions.PointerUp,
                            PixelLocation = new InjectedInputPoint { PositionX = endRawX, PositionY = endRawY }
                        },
                        Pressure = 0.0,
                        TouchParameters = InjectedInputTouchParameters.Pressure | InjectedInputTouchParameters.Contact
                    }});
                    injector.UninitializeTouchInjection();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            else
            {
                // Mouse drag with 0-65535 normalized coordinates
                await _runOnUiThread!(() =>
                {
                    var dispInfo2 = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    double rawW = dispInfo2.ScreenWidthInRawPixels;
                    double rawH = dispInfo2.ScreenHeightInRawPixels;
                    double screenDipW = rawW / dpiScale;
                    double screenDipH = rawH / dpiScale;

                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move | InjectedInputMouseOptions.Absolute,
                        DeltaX = (int)(allPoints[0].X / screenDipW * 65535),
                        DeltaY = (int)(allPoints[0].Y / screenDipH * 65535)
                    }});
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.LeftDown
                    }});
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                for (int i = 1; i < allPoints.Count; i++)
                {
                    await Task.Delay(delayPerPoint).ConfigureAwait(false);
                    int idx = i;
                    await _runOnUiThread!(() =>
                    {
                        var dispInfo2 = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                        double rawW = dispInfo2.ScreenWidthInRawPixels;
                        double rawH = dispInfo2.ScreenHeightInRawPixels;
                        double screenDipW = rawW / dpiScale;
                        double screenDipH = rawH / dpiScale;

                        injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                        {
                            MouseOptions = InjectedInputMouseOptions.Move | InjectedInputMouseOptions.Absolute,
                            DeltaX = (int)(allPoints[idx].X / screenDipW * 65535),
                            DeltaY = (int)(allPoints[idx].Y / screenDipH * 65535)
                        }});
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }

                await _runOnUiThread!(() =>
                {
                    injector.InjectMouseInput(new[] { new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.LeftUp
                    }});
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            return JsonConvert.SerializeObject(new InjectDragPathResponse
            {
                Success = true,
                PointCount = resolvedPath.Count,
                DurationMs = req.DurationMs,
                ResolvedPath = resolvedPath,
                Device = useTouch ? "touch" : "mouse"
            });
        }

        #region Preview screenshot helpers

        /// <summary>
        /// Captures a screenshot and draws a crosshair at the tap location.
        /// Returns the saved file path, or null on failure.
        /// </summary>
        private async Task<string?> CaptureAnnotatedTapPreview(InjectTapRequest req)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? bitmap = null;
            double normX = req.X;
            double normY = req.Y;

            await _runOnUiThread!(async () =>
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);

                // Convert coordinates to normalized if needed
                var space = ParseSpace(req.CoordinateSpace);
                if (space == CoordinateSpace.Client || space == CoordinateSpace.Absolute)
                {
                    var bounds = Window.Current.Bounds;
                    if (space == CoordinateSpace.Client)
                    {
                        normX = req.X / bounds.Width;
                        normY = req.Y / bounds.Height;
                    }
                    else // Absolute
                    {
                        normX = (req.X - bounds.X) / bounds.Width;
                        normY = (req.Y - bounds.Y) / bounds.Height;
                    }
                }
            }).ConfigureAwait(false);

            if (bitmap == null) return null;

            int cx = (int)(normX * bitmap.PixelWidth);
            int cy = (int)(normY * bitmap.PixelHeight);

            var annotated = ScreenshotAnnotator.DrawCrosshair(bitmap, cx, cy);
            annotated = await ScreenshotAnnotator.ResizeBitmapAsync(
                annotated, ScreenshotAnnotator.DefaultMaxDimension, ScreenshotAnnotator.DefaultMaxDimension).ConfigureAwait(false);

            var file = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, "tap_preview").ConfigureAwait(false);
            return file.Path;
        }

        /// <summary>
        /// Captures a screenshot and draws the drag path visualization.
        /// Returns the saved file path, or null on failure.
        /// </summary>
        private async Task<string?> CaptureAnnotatedDragPreview(InjectDragPathRequest req)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? bitmap = null;
            var normalizedPoints = new List<(double x, double y)>();

            await _runOnUiThread!(async () =>
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);

                var space = ParseSpace(req.CoordinateSpace);
                foreach (var pt in req.Points)
                {
                    double nx = pt.X;
                    double ny = pt.Y;
                    if (space == CoordinateSpace.Client || space == CoordinateSpace.Absolute)
                    {
                        var bounds = Window.Current.Bounds;
                        if (space == CoordinateSpace.Client)
                        {
                            nx = pt.X / bounds.Width;
                            ny = pt.Y / bounds.Height;
                        }
                        else
                        {
                            nx = (pt.X - bounds.X) / bounds.Width;
                            ny = (pt.Y - bounds.Y) / bounds.Height;
                        }
                    }
                    normalizedPoints.Add((nx, ny));
                }
            }).ConfigureAwait(false);

            if (bitmap == null || normalizedPoints.Count < 2) return null;

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            var pixelPoints = new List<(int x, int y)>();
            foreach (var (nx, ny) in normalizedPoints)
                pixelPoints.Add(((int)(nx * w), (int)(ny * h)));

            var annotated = ScreenshotAnnotator.DrawDragPath(bitmap, pixelPoints);
            annotated = await ScreenshotAnnotator.ResizeBitmapAsync(
                annotated, ScreenshotAnnotator.DefaultMaxDimension, ScreenshotAnnotator.DefaultMaxDimension).ConfigureAwait(false);

            var file = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, "drag_preview").ConfigureAwait(false);
            return file.Path;
        }

        /// <summary>
        /// Captures a screenshot and draws multiple pointer path visualizations.
        /// Used for multi-touch, pinch, and rotate previews.
        /// Returns the saved file path, or null on failure.
        /// </summary>
        private async Task<string?> CaptureAnnotatedMultiPathPreview(
            List<List<CoordinatePoint>> pointerPaths, string? coordinateSpace, string filePrefix)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? bitmap = null;
            var normalizedPaths = new List<List<(double x, double y)>>();

            await _runOnUiThread!(async () =>
            {
                bitmap = await ScreenshotAnnotator.CaptureUiAsBitmapAsync().ConfigureAwait(false);

                var space = ParseSpace(coordinateSpace);
                foreach (var path in pointerPaths)
                {
                    var nPath = new List<(double x, double y)>();
                    foreach (var pt in path)
                    {
                        double nx = pt.X;
                        double ny = pt.Y;
                        if (space == CoordinateSpace.Client || space == CoordinateSpace.Absolute)
                        {
                            var bounds = Window.Current.Bounds;
                            if (space == CoordinateSpace.Client)
                            {
                                nx = pt.X / bounds.Width;
                                ny = pt.Y / bounds.Height;
                            }
                            else
                            {
                                nx = (pt.X - bounds.X) / bounds.Width;
                                ny = (pt.Y - bounds.Y) / bounds.Height;
                            }
                        }
                        nPath.Add((nx, ny));
                    }
                    normalizedPaths.Add(nPath);
                }
            }).ConfigureAwait(false);

            if (bitmap == null || normalizedPaths.Count == 0) return null;

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            var pixelPaths = new List<List<(int x, int y)>>();
            foreach (var nPath in normalizedPaths)
            {
                var pxPath = new List<(int x, int y)>();
                foreach (var (nx, ny) in nPath)
                    pxPath.Add(((int)(nx * w), (int)(ny * h)));
                pixelPaths.Add(pxPath);
            }

            var annotated = ScreenshotAnnotator.DrawMultiPointerPaths(bitmap, pixelPaths);
            annotated = await ScreenshotAnnotator.ResizeBitmapAsync(
                annotated, ScreenshotAnnotator.DefaultMaxDimension, ScreenshotAnnotator.DefaultMaxDimension).ConfigureAwait(false);

            var file = await ScreenshotAnnotator.SaveScreenshotAsync(annotated, filePrefix).ConfigureAwait(false);
            return file.Path;
        }

        /// <summary>
        /// Injects <c>previewScreenshotPath</c> into an already-serialized JSON response string.
        /// </summary>
        private static string AttachPreviewPath(string resultJson, string? previewPath)
        {
            if (previewPath == null) return resultJson;
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(resultJson);
                obj["previewScreenshotPath"] = previewPath;
                return obj.ToString(Formatting.None);
            }
            catch
            {
                return resultJson;
            }
        }

        #endregion

        private static CoordinateSpace ParseSpace(string? value) =>
            value?.ToLowerInvariant() switch
            {
                "absolute" => CoordinateSpace.Absolute,
                "client" => CoordinateSpace.Client,
                _ => CoordinateSpace.Normalized
            };
    }
}

