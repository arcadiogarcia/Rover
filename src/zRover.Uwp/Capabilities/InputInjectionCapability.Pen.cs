using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Coordinates;
using zRover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace zRover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private void RegisterPenTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_pen_tap",
                "Injects a pen tap at the specified coordinates. " +
                "Supports pressure, tilt, rotation, barrel button, and eraser mode. " +
                "Set hover=true to hover without touching the surface.",
                ToolSchemas.PenTapSchema,
                InjectPenTapAsync);

            registry.RegisterTool(
                "inject_pen_stroke",
                "Injects a pen stroke along a path of points. " +
                "Each point can optionally override pressure, tilt, and rotation for realistic ink strokes. " +
                "Falls back to stroke-level defaults for any unspecified per-point values.",
                ToolSchemas.PenStrokeSchema,
                InjectPenStrokeAsync);
        }

        private InjectedInputPenButtons BuildPenButtons(bool barrel, bool eraser)
        {
            var buttons = (InjectedInputPenButtons)0;
            if (barrel) buttons |= InjectedInputPenButtons.Barrel;
            if (eraser) buttons |= InjectedInputPenButtons.Inverted | InjectedInputPenButtons.Eraser;
            return buttons;
        }

        private async Task<string> InjectPenTapAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectPenTapRequest>(argsJson)
                      ?? new InjectPenTapRequest();

            LogToFile($"InjectPenTapAsync: ({req.X},{req.Y}) pressure={req.Pressure} hover={req.Hover} dryRun={req.DryRun}");

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
                    LogToFile($"PenTap preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectPenTapResponse
                {
                    Success = true,
                    ResolvedCoordinates = new CoordinatePoint(req.X, req.Y),
                    Pressure = req.Pressure,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectPenTapResponse
                {
                    Success = false,
                    Pressure = req.Pressure,
                    PreviewScreenshotPath = previewPath
                });
            }

            CoordinatePoint resolved = new CoordinatePoint(req.X, req.Y);
            Exception? error = null;
            int rawX = 0, rawY = 0;
            var penParams = InjectedInputPenParameters.Pressure
                          | InjectedInputPenParameters.Rotation
                          | InjectedInputPenParameters.TiltX
                          | InjectedInputPenParameters.TiltY;
            var pointerDown = req.Hover
                ? InjectedInputPointerOptions.InRange | InjectedInputPointerOptions.New
                : InjectedInputPointerOptions.PointerDown | InjectedInputPointerOptions.InRange
                  | InjectedInputPointerOptions.InContact | InjectedInputPointerOptions.New
                  | InjectedInputPointerOptions.Primary;
            var pointerUp = req.Hover
                ? InjectedInputPointerOptions.PointerUp
                : InjectedInputPointerOptions.PointerUp | InjectedInputPointerOptions.InRange;

            await _runOnUiThread(() =>
            {
                try
                {
                    try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { }
                    var space = ParseSpace(req.CoordinateSpace);
                    resolved = _resolver!.Resolve(new CoordinatePoint(req.X, req.Y), space);
                    var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                    double dpiScale = dispInfo.RawPixelsPerViewPixel;
                    (rawX, rawY) = ToTouchInjectionPoint(resolved.X, resolved.Y, dpiScale);
                    var penButtons = BuildPenButtons(req.Barrel, req.Eraser);
                    injector.InjectPenInput(new InjectedInputPenInfo
                    {
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = pointerDown,
                            PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                        },
                        Pressure = req.Pressure,
                        TiltX = req.TiltX,
                        TiltY = req.TiltY,
                        Rotation = req.Rotation,
                        PenButtons = BuildPenButtons(req.Barrel, req.Eraser),
                        PenParameters = penParams
                    });
                }
                catch (Exception ex) { error = ex; }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error == null)
            {
                await Task.Delay(50).ConfigureAwait(false);
                await _runOnUiThread(() =>
                {
                    try
                    {
                        injector.InjectPenInput(new InjectedInputPenInfo
                        {
                            PointerInfo = new InjectedInputPointerInfo
                            {
                                PointerId = 1,
                                PointerOptions = pointerUp,
                                PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                            },
                            Pressure = 0.0,
                            PenParameters = InjectedInputPenParameters.Pressure
                        });
                    }
                    catch (Exception ex) { error = ex; }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            if (error != null)
                LogToFile($"PenTap FAILED: {error.Message}");
            else
                LogToFile("PenTap succeeded");

            return JsonConvert.SerializeObject(new InjectPenTapResponse
            {
                Success = error == null,
                ResolvedCoordinates = resolved,
                Pressure = req.Pressure,
                PreviewScreenshotPath = previewPath
            });
        }

        private async Task<string> InjectPenStrokeAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectPenStrokeRequest>(argsJson)
                      ?? new InjectPenStrokeRequest();

            LogToFile($"InjectPenStrokeAsync: {req.Points.Count} points, duration={req.DurationMs}ms dryRun={req.DryRun}");

            // Capture preview using the drag path annotation (pen stroke points → CoordinatePoints)
            string? previewPath = null;
            if (_runOnUiThread != null && req.Points.Count >= 2)
            {
                try
                {
                    previewPath = await CaptureAnnotatedDragPreview(new InjectDragPathRequest
                    {
                        Points = req.Points.Select(p => new CoordinatePoint(p.X, p.Y)).ToList(),
                        CoordinateSpace = req.CoordinateSpace
                    }).ConfigureAwait(false);
                    LogToFile($"PenStroke preview: {previewPath}");
                }
                catch (Exception ex)
                {
                    LogToFile($"PenStroke preview failed: {ex.Message}");
                }
            }

            if (req.DryRun)
            {
                return JsonConvert.SerializeObject(new InjectPenStrokeResponse
                {
                    Success = true,
                    PointCount = req.Points.Count,
                    DurationMs = req.DurationMs,
                    PreviewScreenshotPath = previewPath,
                    DryRun = true
                });
            }

            var injector = _injector;
            if (injector == null || _runOnUiThread == null || req.Points.Count < 2)
            {
                return InjectorUnavailableResponse(new InjectPenStrokeResponse
                {
                    Success = false,
                    PointCount = req.Points.Count,
                    DurationMs = req.DurationMs,
                    PreviewScreenshotPath = previewPath
                });
            }

            Exception? error = null;
            try
            {
                await InjectPenStrokeGestureAsync(injector, req).ConfigureAwait(false);
            }
            catch (Exception ex) { error = ex; }

            if (error != null)
                LogToFile($"PenStroke FAILED: {error.Message}");
            else
                LogToFile("PenStroke succeeded");

            var result = JsonConvert.SerializeObject(new InjectPenStrokeResponse
            {
                Success = error == null,
                PointCount = req.Points.Count,
                DurationMs = req.DurationMs,
                PreviewScreenshotPath = previewPath
            });
            return result;
        }

        private async Task InjectPenStrokeGestureAsync(InputInjector injector, InjectPenStrokeRequest req)
        {
            var space = ParseSpace(req.CoordinateSpace);
            double dpiScale = 0;

            var penButtons = BuildPenButtons(req.Barrel, req.Eraser);
            var penParams = InjectedInputPenParameters.Pressure
                          | InjectedInputPenParameters.Rotation
                          | InjectedInputPenParameters.TiltX
                          | InjectedInputPenParameters.TiltY;

            // Resolve all requested points on the UI thread; callers supply enough points.
            var resolvedPoints = new List<(double x, double y, double pressure)>();
            await _runOnUiThread!(() =>
            {
                try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { }
                var dispInfo = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                dpiScale = dispInfo.RawPixelsPerViewPixel;
                for (int i = 0; i < req.Points.Count; i++)
                {
                    var p = req.Points[i];
                    var res = _resolver!.Resolve(new CoordinatePoint(p.X, p.Y), space);
                    resolvedPoints.Add((res.X, res.Y, p.Pressure ?? req.Pressure));
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            int delayPerPoint = Math.Max(1, req.DurationMs / Math.Max(1, resolvedPoints.Count - 1));

            // Pen down at first point
            await _runOnUiThread!(() =>
            {
                var (rawX, rawY) = ToTouchInjectionPoint(resolvedPoints[0].x, resolvedPoints[0].y, dpiScale);
                injector.InjectPenInput(new InjectedInputPenInfo
                {
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = 1,
                        PointerOptions = InjectedInputPointerOptions.PointerDown
                                       | InjectedInputPointerOptions.InRange
                                       | InjectedInputPointerOptions.InContact
                                       | InjectedInputPointerOptions.New
                                       | InjectedInputPointerOptions.Primary,
                        PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                    },
                    Pressure = resolvedPoints[0].pressure,
                    TiltX = req.TiltX,
                    TiltY = req.TiltY,
                    Rotation = req.Rotation,
                    PenButtons = penButtons,
                    PenParameters = penParams
                });
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // Pen move — one Update per intermediate point
            for (int i = 1; i < resolvedPoints.Count - 1; i++)
            {
                await Task.Delay(delayPerPoint).ConfigureAwait(false);
                int idx = i;
                await _runOnUiThread!(() =>
                {
                    var (ptX, ptY, ptP) = resolvedPoints[idx];
                    var (rawX, rawY) = ToTouchInjectionPoint(ptX, ptY, dpiScale);
                    injector.InjectPenInput(new InjectedInputPenInfo
                    {
                        PointerInfo = new InjectedInputPointerInfo
                        {
                            PointerId = 1,
                            PointerOptions = InjectedInputPointerOptions.InRange
                                           | InjectedInputPointerOptions.InContact
                                           | InjectedInputPointerOptions.Update
                                           | InjectedInputPointerOptions.Primary,
                            PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                        },
                        Pressure = ptP,
                        TiltX = req.TiltX,
                        TiltY = req.TiltY,
                        Rotation = req.Rotation,
                        PenButtons = penButtons,
                        PenParameters = penParams
                    });
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            // Pen up at last point
            await Task.Delay(delayPerPoint).ConfigureAwait(false);
            var last = resolvedPoints[resolvedPoints.Count - 1];
            await _runOnUiThread!(() =>
            {
                var (rawX, rawY) = ToTouchInjectionPoint(last.x, last.y, dpiScale);
                injector.InjectPenInput(new InjectedInputPenInfo
                {
                    PointerInfo = new InjectedInputPointerInfo
                    {
                        PointerId = 1,
                        PointerOptions = InjectedInputPointerOptions.PointerUp | InjectedInputPointerOptions.InRange,
                        PixelLocation = new InjectedInputPoint { PositionX = rawX, PositionY = rawY }
                    },
                    Pressure = 0.0,
                    PenParameters = InjectedInputPenParameters.Pressure
                });
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
