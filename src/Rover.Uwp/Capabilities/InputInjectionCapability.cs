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
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Automation.Provider;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace Rover.Uwp.Capabilities
{
    public sealed class InputInjectionCapability : IDebugCapability
    {
        private InputInjector? _injector;
        private ICoordinateResolver? _resolver;
        private Func<Func<Task>, Task>? _runOnUiThread;

        private const string TapSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate. In the default normalized space this is 0.0 (left) to 1.0 (right)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate. In the default normalized space this is 0.0 (top) to 1.0 (bottom)."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""absolute"", ""normalized"", ""client""], ""default"": ""normalized"", ""description"": ""Coordinate space: 'normalized' (default, 0-1 relative to app window), 'client' (window pixels), or 'absolute' (screen pixels)."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" }
  },
  ""required"": [""x"", ""y""]
}";

        private const string DragSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""points"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/$defs/point"" }, ""minItems"": 2, ""description"": ""Ordered waypoints for the drag gesture."" },
    ""durationMs"": { ""type"": ""integer"", ""default"": 300, ""description"": ""Total duration of the drag in milliseconds."" },
    ""coordinateSpace"": { ""type"": ""string"", ""enum"": [""absolute"", ""normalized"", ""client""], ""default"": ""normalized"", ""description"": ""Coordinate space: 'normalized' (default, 0-1 relative to app window), 'client' (window pixels), or 'absolute' (screen pixels)."" },
    ""device"": { ""type"": ""string"", ""enum"": [""touch"", ""mouse""], ""default"": ""touch"" }
  },
  ""required"": [""points""],
  ""$defs"": { ""point"": { ""type"": ""object"", ""properties"": { ""x"": {""type"":""number"", ""description"": ""X position (0.0–1.0 in normalized space)""}, ""y"": {""type"":""number"", ""description"": ""Y position (0.0–1.0 in normalized space)""} }, ""required"": [""x"",""y""] } }
}";

        public string Name => "InputInjection";

        public async Task StartAsync(DebugHostContext context)
        {
            _resolver = context.CoordinateResolver;
            _runOnUiThread = context.RunOnUiThread;

            // InputInjector must be created on the UI thread.
            // NOTE: InputInjector.TryCreate() can fail with COM errors on some systems (0x800700C1)
            // due to architecture mismatches. We handle this gracefully and log the error.
            if (context.RunOnUiThread != null)
            {
                await context.RunOnUiThread(() =>
                {
                    try
                    {
                        _injector = InputInjector.TryCreate();
                        
                        if (_injector == null)
                        {
                            System.Diagnostics.Debug.WriteLine("[InputInjection] Failed to create InputInjector (returned null)");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[InputInjection] InputInjector created successfully");
                        }
                    }
                    catch (InvalidCastException ex)
                    {
                        // Common on x64 builds due to COM architecture mismatch (HRESULT 0x800700C1)
                        System.Diagnostics.Debug.WriteLine($"[InputInjection] COM cast error creating InputInjector: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine("[InputInjection] Input injection will not be available");
                        _injector = null;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InputInjection] Unexpected error creating InputInjector: {ex}");
                        _injector = null;
                    }
                    
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    _injector = InputInjector.TryCreate();
                    
                    if (_injector == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[InputInjection] Failed to create InputInjector (returned null)");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[InputInjection] InputInjector created successfully");
                    }
                }
                catch (InvalidCastException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InputInjection] COM cast error creating InputInjector: {ex.Message}");
                    _injector = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InputInjection] Unexpected error creating InputInjector: {ex}");
                    _injector = null;
                }
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
                "Injects a tap/touch event at the specified coordinates. Coordinates default to normalized space (0.0–1.0 relative to the app window), where (0,0) is the top-left corner and (1,1) is the bottom-right. Use capture_current_view first to see the UI layout.",
                TapSchema,
                InjectTapAsync);

            registry.RegisterTool(
                "inject_drag_path",
                "Injects a drag gesture along a path of points. Coordinates default to normalized space (0.0–1.0 relative to the app window), where (0,0) is the top-left corner and (1,1) is the bottom-right. Use capture_current_view first to see the UI layout.",
                DragSchema,
                InjectDragPathAsync);
        }

        private async Task<string> InjectTapAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectTapRequest>(argsJson)
                      ?? new InjectTapRequest();

            var injector = _injector;
            if (injector != null)
            {
                return InjectTapViaInjector(injector, req);
            }

            // Fallback: use XAML automation peers when InputInjector is not available
            if (_runOnUiThread != null)
            {
                return await InjectTapViaAutomation(req).ConfigureAwait(false);
            }

            var errorResponse = new InjectTapResponse
            {
                Success = false,
                Device = req.Device,
                ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                Timestamp = DateTimeOffset.UtcNow.ToString("o")
            };
            System.Diagnostics.Debug.WriteLine("[InputInjection] Cannot inject tap - no injection method available");
            return JsonConvert.SerializeObject(errorResponse);
        }

        private string InjectTapViaInjector(InputInjector injector, InjectTapRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);
            var resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);

            injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);

            var info = new InjectedInputTouchInfo
            {
                PointerInfo = new InjectedInputPointerInfo
                {
                    PointerId = 0,
                    PointerOptions = InjectedInputPointerOptions.New
                                   | InjectedInputPointerOptions.InContact
                                   | InjectedInputPointerOptions.FirstButton,
                    PixelLocation = new InjectedInputPoint
                    {
                        PositionX = (int)resolved.X,
                        PositionY = (int)resolved.Y
                    }
                },
                TouchParameters = InjectedInputTouchParameters.None
            };

            injector.InjectTouchInput(new[] { info });

            var liftInfo = new InjectedInputTouchInfo
            {
                PointerInfo = new InjectedInputPointerInfo
                {
                    PointerId = 0,
                    PointerOptions = InjectedInputPointerOptions.None,
                    PixelLocation = new InjectedInputPoint
                    {
                        PositionX = (int)resolved.X,
                        PositionY = (int)resolved.Y
                    }
                },
                TouchParameters = InjectedInputTouchParameters.None
            };
            injector.InjectTouchInput(new[] { liftInfo });

            var response = new InjectTapResponse
            {
                Success = true,
                ResolvedCoordinates = resolved,
                Device = req.Device
            };
            return JsonConvert.SerializeObject(response);
        }

        private async Task<string> InjectTapViaAutomation(InjectTapRequest req)
        {
            bool success = false;
            CoordinatePoint clientPt = new CoordinatePoint(req.X, req.Y);

            await _runOnUiThread!(async () =>
            {
                var root = Window.Current.Content as UIElement;
                if (root == null) return;

                // Convert normalized coordinates to view-pixel coordinates
                var bounds = Window.Current.Bounds;
                double viewX = req.X * bounds.Width;
                double viewY = req.Y * bounds.Height;
                var space = ParseSpace(req.CoordinateSpace);
                if (space == CoordinateSpace.Absolute || space == CoordinateSpace.Client)
                {
                    viewX = req.X;
                    viewY = req.Y;
                }

                clientPt = new CoordinatePoint(viewX, viewY);
                var point = new Point(viewX, viewY);

                var elements = VisualTreeHelper.FindElementsInHostCoordinates(point, root);
                foreach (var element in elements)
                {
                    // Try Button first
                    if (element is ButtonBase button)
                    {
                        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
                        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
                        {
                            invoker.Invoke();
                            success = true;
                            return;
                        }
                    }
                }

                await Task.CompletedTask;
            }).ConfigureAwait(false);

            var response = new InjectTapResponse
            {
                Success = success,
                ResolvedCoordinates = clientPt,
                Device = req.Device
            };
            return JsonConvert.SerializeObject(response);
        }

        private async Task<string> InjectDragPathAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectDragPathRequest>(argsJson)
                      ?? new InjectDragPathRequest();

            var injector = _injector;
            if (injector != null)
            {
                return await InjectDragViaInjector(injector, req).ConfigureAwait(false);
            }

            // Fallback: use XAML automation peers for Slider manipulation
            if (_runOnUiThread != null)
            {
                return await InjectDragViaAutomation(req).ConfigureAwait(false);
            }

            var errorResponse = new InjectDragPathResponse
            {
                Success = false,
                Device = req.Device,
                DurationMs = req.DurationMs,
                PointCount = 0,
                ResolvedPath = new List<CoordinatePoint>()
            };
            System.Diagnostics.Debug.WriteLine("[InputInjection] Cannot inject drag - no injection method available");
            return JsonConvert.SerializeObject(errorResponse);
        }

        private async Task<string> InjectDragViaInjector(InputInjector injector, InjectDragPathRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);
            var resolvedPath = new List<CoordinatePoint>();
            foreach (var pt in req.Points)
                resolvedPath.Add(_resolver!.Resolve(pt, space));

            if (resolvedPath.Count < 2)
            {
                return JsonConvert.SerializeObject(new InjectDragPathResponse { Success = false });
            }

            injector.InitializeTouchInjection(InjectedInputVisualizationMode.Default);

            int stepCount = resolvedPath.Count;
            int delayPerStep = Math.Max(1, req.DurationMs / Math.Max(1, stepCount - 1));

            for (int i = 0; i < stepCount; i++)
            {
                var options = i == 0
                    ? InjectedInputPointerOptions.New | InjectedInputPointerOptions.InContact | InjectedInputPointerOptions.FirstButton
                    : i == stepCount - 1
                        ? InjectedInputPointerOptions.None
                        : InjectedInputPointerOptions.Update | InjectedInputPointerOptions.InContact | InjectedInputPointerOptions.FirstButton;

                var info = new InjectedInputTouchInfo
                {
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = 0,
                        PointerOptions = options,
                        PixelLocation = new InjectedInputPoint
                        {
                            PositionX = (int)resolvedPath[i].X,
                            PositionY = (int)resolvedPath[i].Y
                        }
                    },
                    TouchParameters = InjectedInputTouchParameters.None
                };

                injector.InjectTouchInput(new[] { info });

                if (i < stepCount - 1)
                    await Task.Delay(delayPerStep).ConfigureAwait(false);
            }

            var response = new InjectDragPathResponse
            {
                Success = true,
                PointCount = resolvedPath.Count,
                DurationMs = req.DurationMs,
                ResolvedPath = resolvedPath,
                Device = req.Device
            };
            return JsonConvert.SerializeObject(response);
        }

        private async Task<string> InjectDragViaAutomation(InjectDragPathRequest req)
        {
            bool success = false;
            var resolvedPath = new List<CoordinatePoint>();

            await _runOnUiThread!(async () =>
            {
                var root = Window.Current.Content as UIElement;
                if (root == null || req.Points.Count < 2) return;

                var bounds = Window.Current.Bounds;
                var space = ParseSpace(req.CoordinateSpace);

                // Convert all points to view-pixel coordinates
                var viewPoints = new List<Point>();
                foreach (var pt in req.Points)
                {
                    double vx, vy;
                    if (space == CoordinateSpace.Normalized)
                    {
                        vx = pt.X * bounds.Width;
                        vy = pt.Y * bounds.Height;
                    }
                    else
                    {
                        vx = pt.X;
                        vy = pt.Y;
                    }
                    viewPoints.Add(new Point(vx, vy));
                    resolvedPath.Add(new CoordinatePoint(vx, vy));
                }

                // Find element at the start point
                var startPoint = viewPoints[0];
                var elements = VisualTreeHelper.FindElementsInHostCoordinates(startPoint, root);

                foreach (var element in elements)
                {
                    if (element is Slider slider)
                    {
                        // Calculate value from the end point relative to the slider's bounds
                        var sliderBounds = element.TransformToVisual(root).TransformBounds(
                            new Rect(0, 0, slider.ActualWidth, slider.ActualHeight));

                        var endPoint = viewPoints[viewPoints.Count - 1];
                        double fraction = (endPoint.X - sliderBounds.X) / sliderBounds.Width;
                        fraction = Math.Max(0, Math.Min(1, fraction));

                        double newValue = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);

                        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(slider);
                        if (peer?.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider rangeProvider)
                        {
                            rangeProvider.SetValue(newValue);
                            success = true;
                            return;
                        }

                        // Direct fallback if peer doesn't work
                        slider.Value = newValue;
                        success = true;
                        return;
                    }
                }

                await Task.CompletedTask;
            }).ConfigureAwait(false);

            var response = new InjectDragPathResponse
            {
                Success = success,
                PointCount = resolvedPath.Count,
                DurationMs = req.DurationMs,
                ResolvedPath = resolvedPath,
                Device = req.Device
            };
            return JsonConvert.SerializeObject(response);
        }

        private static CoordinateSpace ParseSpace(string? value) =>
            value?.ToLowerInvariant() switch
            {
                "absolute" => CoordinateSpace.Absolute,
                "client" => CoordinateSpace.Client,
                _ => CoordinateSpace.Normalized
            };
    }
}

