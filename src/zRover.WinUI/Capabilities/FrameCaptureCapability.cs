using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Newtonsoft.Json;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using zRover.Core;
using zRover.Core.Logging;
using zRover.Core.Tools.Screenshot;

namespace zRover.WinUI.Capabilities
{
    /// <summary>
    /// Exposes the <c>capture_frames</c> tool, which records a burst of
    /// compositor frames via <see cref="Direct3D11CaptureFramePool"/> and
    /// returns each frame as a PNG on disk together with precise
    /// <see cref="Direct3D11CaptureFrame.SystemRelativeTime"/> timestamps.
    ///
    /// Use case: diagnosing animations, frame timing irregularities, dropped
    /// frames, and flicker that a single screenshot cannot reveal.
    /// </summary>
    internal sealed class FrameCaptureCapability : IDebugCapability
    {
        private const int DefaultFrameCount = 30;
        private const int MaxFrameCount = 240;
        private const int DefaultMaxDurationMs = 4000;
        private const int HardMaxDurationMs = 30_000;
        private const int DefaultMaxDimension = 1280;
        private const int FramePoolSize = 10;

        private const string Schema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""frameCount"":     { ""type"": ""integer"", ""description"": ""Number of compositor frames to capture. Capture stops as soon as this many frames are recorded or maxDurationMs elapses, whichever comes first. Default: 30, max: 240."", ""default"": 30 },
    ""maxDurationMs"":  { ""type"": ""integer"", ""description"": ""Hard upper bound on capture wall-clock duration in ms. Default: 4000, max: 30000."", ""default"": 4000 },
    ""maxWidth"":       { ""type"": ""integer"", ""description"": ""Maximum width of each saved PNG in pixels. Frames are scaled down proportionally before encoding. Default: 1280."", ""default"": 1280 },
    ""maxHeight"":      { ""type"": ""integer"", ""description"": ""Maximum height of each saved PNG in pixels. Default: 1280."", ""default"": 1280 },
    ""includeCursor"":  { ""type"": ""boolean"", ""description"": ""When true, include the OS mouse cursor in the captured frames. Default: false."", ""default"": false }
  }
}";

        private DebugHostContext? _context;
        private readonly Window _window;

        public string Name => "FrameCapture";

        public FrameCaptureCapability(Window window)
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

        private const string ReadFrameSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""Absolute path to a PNG previously written by capture_frames. Must reside under the host app's debug-artifacts/screenshots/ directory."" }
  },
  ""required"": [""path""]
}";

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "capture_frames",
                "Records a burst of compositor frames from the WinUI 3 app window using the " +
                "Windows.Graphics.Capture API (the same pipeline used by OBS and Xbox Game Bar) and writes " +
                "each frame to disk as a PNG. Each frame carries the compositor's exact " +
                "SystemRelativeTime in microseconds, allowing precise diagnosis of animations, " +
                "dropped frames, jank, and flicker that a single screenshot cannot reveal. " +
                "Capture stops when frameCount frames have been recorded or maxDurationMs elapses, " +
                "whichever comes first. " +
                "Returns a JSON manifest with the absolute output directory, per-frame paths, " +
                "per-frame compositor timestamps, inter-frame deltas, and average FPS. " +
                "PNGs are written under the host app's debug-artifacts/screenshots/frames-{sessionId}/ directory. " +
                "Use the companion read_capture_frame tool to retrieve any single frame inline as an image.",
                Schema,
                CaptureFramesAsync);

            registry.RegisterTool(
                "read_capture_frame",
                "Reads a single PNG previously written by capture_frames and returns it inline as an image " +
                "content block, so that an agent can visually inspect specific frames from a burst capture " +
                "(e.g. to diagnose a glitch at a particular timestamp). " +
                "The path must point to a file under the host app's debug-artifacts/screenshots/ directory.",
                ReadFrameSchema,
                ReadCaptureFrameAsync);
        }

        private Task<RoverToolResult> ReadCaptureFrameAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<ReadCaptureFrameRequest>(argsJson)
                          ?? new ReadCaptureFrameRequest();
                if (string.IsNullOrWhiteSpace(req.Path))
                    return Task.FromResult(RoverToolResult.FromText(JsonConvert.SerializeObject(new { success = false, error = "path is required." })));

                string artifactRoot = Path.GetFullPath(Path.Combine(_context!.ArtifactDirectory, "screenshots"));
                string fullPath = Path.GetFullPath(req.Path!);

                if (!fullPath.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(RoverToolResult.FromText(JsonConvert.SerializeObject(new { success = false, error = $"path must be under {artifactRoot}" })));

                if (!File.Exists(fullPath))
                    return Task.FromResult(RoverToolResult.FromText(JsonConvert.SerializeObject(new { success = false, error = $"file not found: {fullPath}" })));

                byte[] png = File.ReadAllBytes(fullPath);
                string meta = JsonConvert.SerializeObject(new
                {
                    success = true,
                    path = fullPath,
                    sizeBytes = png.LongLength,
                });
                return Task.FromResult(RoverToolResult.WithImage(meta, png));
            }
            catch (Exception ex)
            {
                RoverLog.Error("zRover.FrameCapture", $"read_capture_frame failed: {ex}");
                return Task.FromResult(RoverToolResult.FromText(JsonConvert.SerializeObject(new { success = false, error = ex.Message })));
            }
        }

        private sealed class ReadCaptureFrameRequest
        {
            [JsonProperty("path")]
            public string? Path { get; set; }
        }

        private async Task<string> CaptureFramesAsync(string argsJson)
        {
            var response = new CaptureFramesResponse();
            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    response.Success = false;
                    response.Error = "Windows.Graphics.Capture is not supported on this OS.";
                    return JsonConvert.SerializeObject(response);
                }

                var req = JsonConvert.DeserializeObject<CaptureFramesRequest>(argsJson)
                          ?? new CaptureFramesRequest();

                int frameCount = Math.Max(1, Math.Min(MaxFrameCount, req.FrameCount ?? DefaultFrameCount));
                int maxDurationMs = Math.Max(50, Math.Min(HardMaxDurationMs, req.MaxDurationMs ?? DefaultMaxDurationMs));
                int maxW = Math.Max(16, req.MaxWidth ?? DefaultMaxDimension);
                int maxH = Math.Max(16, req.MaxHeight ?? DefaultMaxDimension);
                bool includeCursor = req.IncludeCursor ?? false;

                response.RequestedFrameCount = frameCount;

                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                if (hwnd == IntPtr.Zero)
                    throw new InvalidOperationException("Window HWND is not available.");

                var collected = await CaptureFramesCoreAsync(hwnd, frameCount, maxDurationMs, includeCursor)
                    .ConfigureAwait(false);

                if (collected.Frames.Count == 0)
                {
                    response.Success = false;
                    response.Error = "No frames were delivered by the compositor before the timeout. " +
                                     "Ensure the window is visible and the OS supports Windows.Graphics.Capture.";
                    response.WindowWidth = collected.InitialSize.Width;
                    response.WindowHeight = collected.InitialSize.Height;
                    return JsonConvert.SerializeObject(response);
                }

                // Assemble session output directory.
                string sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                string baseDir = Path.Combine(_context!.ArtifactDirectory, "screenshots", $"frames-{sessionId}");
                Directory.CreateDirectory(baseDir);

                response.SessionId = sessionId;
                response.Directory = baseDir;
                response.WindowWidth = collected.InitialSize.Width;
                response.WindowHeight = collected.InitialSize.Height;

                // Encode + write all frames. First frame seeds bitmap dims used in response.
                long firstTimestampUs = collected.Frames[0].SystemRelativeTimeUs;
                long lastTimestampUs = firstTimestampUs;
                int writtenBitmapWidth = 0;
                int writtenBitmapHeight = 0;

                for (int i = 0; i < collected.Frames.Count; i++)
                {
                    var raw = collected.Frames[i];
                    SoftwareBitmap bgra = SoftwareBitmap.Convert(raw.Bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    raw.Bitmap.Dispose();

                    SoftwareBitmap encoded = await ScreenshotAnnotator.ResizeBitmapAsync(bgra, maxW, maxH).ConfigureAwait(false);
                    byte[] png = await ScreenshotAnnotator.EncodeToPngBytesAsync(encoded).ConfigureAwait(false);

                    if (i == 0)
                    {
                        writtenBitmapWidth = encoded.PixelWidth;
                        writtenBitmapHeight = encoded.PixelHeight;
                    }

                    string filename = $"frame-{i:D4}.png";
                    string fullPath = Path.Combine(baseDir, filename);
                    File.WriteAllBytes(fullPath, png);

                    double deltaMs = i == 0
                        ? 0.0
                        : (raw.SystemRelativeTimeUs - collected.Frames[i - 1].SystemRelativeTimeUs) / 1000.0;

                    response.Frames.Add(new CaptureFrameInfo
                    {
                        Index = i,
                        Path = fullPath,
                        SystemRelativeTimeUs = raw.SystemRelativeTimeUs,
                        DeltaFromPreviousMs = deltaMs,
                        ContentWidth = raw.ContentWidth,
                        ContentHeight = raw.ContentHeight,
                    });

                    lastTimestampUs = raw.SystemRelativeTimeUs;

                    if (encoded != bgra) encoded.Dispose();
                    bgra.Dispose();
                }

                int captured = response.Frames.Count;
                double totalDurationMs = (lastTimestampUs - firstTimestampUs) / 1000.0;
                double avgIntervalMs = captured > 1 ? totalDurationMs / (captured - 1) : 0.0;
                double avgFps = avgIntervalMs > 0 ? 1000.0 / avgIntervalMs : 0.0;

                response.Success = true;
                response.CapturedFrameCount = captured;
                response.TotalDurationMs = totalDurationMs;
                response.AverageFrameIntervalMs = avgIntervalMs;
                response.AverageFps = avgFps;
                response.BitmapWidth = writtenBitmapWidth;
                response.BitmapHeight = writtenBitmapHeight;

                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                RoverLog.Error("zRover.FrameCapture", $"capture_frames failed: {ex}");
                response.Success = false;
                response.Error = ex.Message;
                return JsonConvert.SerializeObject(response);
            }
        }

        private readonly struct CapturedFrame
        {
            public CapturedFrame(SoftwareBitmap bitmap, long systemRelativeTimeUs, int contentWidth, int contentHeight)
            {
                Bitmap = bitmap;
                SystemRelativeTimeUs = systemRelativeTimeUs;
                ContentWidth = contentWidth;
                ContentHeight = contentHeight;
            }

            public SoftwareBitmap Bitmap { get; }
            public long SystemRelativeTimeUs { get; }
            public int ContentWidth { get; }
            public int ContentHeight { get; }
        }

        private sealed class CaptureCollection
        {
            public List<CapturedFrame> Frames { get; } = new List<CapturedFrame>();
            public SizeInt32 InitialSize;
        }

        private static async Task<CaptureCollection> CaptureFramesCoreAsync(
            IntPtr hwnd,
            int frameCount,
            int maxDurationMs,
            bool includeCursor)
        {
            var item = FrameCaptureHelpers.CreateItemForWindow(hwnd);
            if (item == null)
                throw new InvalidOperationException("GraphicsCaptureItem.CreateForWindow returned null. The window may not allow capture.");

            var device = FrameCaptureHelpers.CreateDirect3DDevice();

            var collection = new CaptureCollection { InitialSize = item.Size };
            var initialSize = item.Size;
            // Capture pool requires non-zero dimensions.
            if (initialSize.Width <= 0 || initialSize.Height <= 0)
                throw new InvalidOperationException($"Window content size is invalid for capture: {initialSize.Width}x{initialSize.Height}.");

            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolSize,
                initialSize);

            using var done = new SemaphoreSlim(0, 1);
            int countLocal = 0;
            object frameLock = new object();
            Exception? capturedException = null;

            Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> onFrame = async (pool, _) =>
            {
                try
                {
                    using var frame = pool.TryGetNextFrame();
                    if (frame == null) return;

                    long usTimestamp = frame.SystemRelativeTime.Ticks / 10; // 100ns ticks -> microseconds
                    int cw = frame.ContentSize.Width;
                    int ch = frame.ContentSize.Height;

                    SoftwareBitmap bmp;
                    try
                    {
                        bmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                            frame.Surface,
                            BitmapAlphaMode.Premultiplied);
                    }
                    catch (Exception copyEx)
                    {
                        // Surface may have transient issues during a resize; skip this frame.
                        Debug.WriteLine($"[zRover.FrameCapture] CreateCopyFromSurfaceAsync failed: {copyEx.Message}");
                        return;
                    }

                    bool finished = false;
                    lock (frameLock)
                    {
                        if (collection.Frames.Count >= frameCount)
                        {
                            bmp.Dispose();
                            return;
                        }
                        collection.Frames.Add(new CapturedFrame(bmp, usTimestamp, cw, ch));
                        countLocal = collection.Frames.Count;
                        if (countLocal >= frameCount)
                            finished = true;
                    }

                    if (finished)
                    {
                        try { done.Release(); } catch (SemaphoreFullException) { }
                    }
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    try { done.Release(); } catch (SemaphoreFullException) { }
                }
            };

            framePool.FrameArrived += onFrame;

            GraphicsCaptureSession? session = null;
            try
            {
                session = framePool.CreateCaptureSession(item);

                // Cursor capture toggle (available on supported builds).
                try { session.IsCursorCaptureEnabled = includeCursor; }
                catch { /* Older OS builds may not expose this property. */ }

                // Suppress the yellow capture border when supported
                // (Windows 11 22H2+). Reflection-based to remain compatible with
                // older WinAppSDK build targets.
                try
                {
                    var sessionType = session.GetType();
                    var borderProp = sessionType.GetProperty("IsBorderRequired");
                    if (borderProp != null && borderProp.CanWrite)
                        borderProp.SetValue(session, false);
                }
                catch { /* property unavailable on this OS build */ }

                session.StartCapture();

                bool reachedTarget = await done.WaitAsync(maxDurationMs).ConfigureAwait(false);
                if (capturedException != null)
                    throw capturedException;
                _ = reachedTarget; // either way we stop and return what we have
            }
            finally
            {
                framePool.FrameArrived -= onFrame;
                session?.Dispose();
                framePool.Dispose();
                try { (device as IDisposable)?.Dispose(); } catch { }
            }

            return collection;
        }
    }

}
