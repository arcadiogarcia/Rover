using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Tools.Screenshot;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Rover.Uwp.Capabilities
{
    public sealed class ScreenshotCapability : IDebugCapability
    {
        private const int DefaultMaxDimension = 1280;

        private const string CaptureSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""format"": { ""type"": ""string"", ""enum"": [""png""], ""default"": ""png"" },
    ""maxWidth"": { ""type"": ""integer"", ""description"": ""Maximum width of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 },
    ""maxHeight"": { ""type"": ""integer"", ""description"": ""Maximum height of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 }
  }
}";

        private const string RegionSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""Left edge in normalized coordinates (0.0–1.0)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Top edge in normalized coordinates (0.0–1.0)."" },
    ""width"": { ""type"": ""number"", ""description"": ""Width of the region in normalized coordinates (0.0–1.0)."" },
    ""height"": { ""type"": ""number"", ""description"": ""Height of the region in normalized coordinates (0.0–1.0)."" },
    ""maxWidth"": { ""type"": ""integer"", ""description"": ""Maximum width of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 },
    ""maxHeight"": { ""type"": ""integer"", ""description"": ""Maximum height of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 }
  },
  ""required"": [""x"", ""y"", ""width"", ""height""]
}";

        private const string ValidatePositionSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""description"": ""X coordinate in normalized space (0.0–1.0)."" },
    ""y"": { ""type"": ""number"", ""description"": ""Y coordinate in normalized space (0.0–1.0)."" },
    ""maxWidth"": { ""type"": ""integer"", ""description"": ""Maximum width of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 },
    ""maxHeight"": { ""type"": ""integer"", ""description"": ""Maximum height of the returned image in pixels. The image is scaled down proportionally if it exceeds this limit. Default: 1280."", ""default"": 1280 }
  },
  ""required"": [""x"", ""y""]
}";

        private DebugHostContext? _context;

        public string Name => "Screenshot";

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

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "capture_current_view",
                "Captures the current app window as a PNG screenshot and returns its file path and dimensions (in render pixels). " +
                "To convert a screenshot pixel position (px, py) to normalized coordinates for inject_tap/inject_drag_path: x = px / width, y = py / height. " +
                "For example, if the screenshot is 1024×768 and you want to tap at pixel (512, 384), use normalized (0.5, 0.5). " +
                "Do NOT pass raw pixel values to injection tools — always divide by the screenshot dimensions first. " +
                "If you are unsure about precise element positions, use capture_region to zoom into a smaller area and verify before interacting.",
                CaptureSchema,
                CaptureAsync);

            registry.RegisterTool(
                "validate_position",
                "Captures the current app window and draws a high-visibility crosshair marker at the specified normalized coordinates."+
                " Use this BEFORE calling inject_tap or inject_drag_path to visually confirm that your estimated coordinates land on the intended UI element. " +
                "The crosshair is drawn with contrasting black and cyan outlines so it is visible on any background. " +
                "Returns the annotated screenshot file path and the marker position.",
                ValidatePositionSchema,
                ValidatePositionAsync);

            registry.RegisterTool(
                "capture_region",
                "Captures a cropped region of the app window as a PNG screenshot. Coordinates are in normalized space (0.0–1.0). " +
                "PURPOSE: Use this tool to verify and refine coordinates you have estimated from a full screenshot before injecting taps or drags. " +
                "Eyeballing positions from a full-window screenshot is error-prone — this tool lets you zoom in on a smaller area to confirm " +
                "exactly where UI elements are, then adjust your coordinates accordingly. " +
                "WORKFLOW: (1) Call capture_current_view to see the full UI. (2) Estimate the normalized region containing your target element. " +
                "(3) Call capture_region with that region to get a zoomed-in view. (4) Inspect the cropped image to verify element positions. " +
                "(5) If needed, adjust and capture again. (6) Once confident, use inject_tap or inject_drag_path with the confirmed coordinates. " +
                "COORDINATE CONVERSION: The response includes fullWidth/fullHeight (the full screenshot dimensions) and the normalizedRegion you requested. " +
                "To convert a pixel position (px, py) within the cropped image to normalized coordinates for injection: " +
                "normalizedX = region.x + (px / fullWidth), normalizedY = region.y + (py / fullHeight).",
                RegionSchema,
                CaptureRegionAsync);
        }

        private async Task<string> CaptureAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<CaptureViewRequest>(argsJson)
                          ?? new CaptureViewRequest();
                int maxW = req.MaxWidth ?? DefaultMaxDimension;
                int maxH = req.MaxHeight ?? DefaultMaxDimension;

                var fileName = $"frame_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";

                // RenderTargetBitmap must be created and used on the UI thread.
                SoftwareBitmap? bitmap = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(async () =>
                    {
                        bitmap = await CaptureUiAsBitmapAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    bitmap = await CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }

                if (bitmap == null)
                    throw new InvalidOperationException("Capture returned no frame.");

                bitmap = await ResizeBitmapAsync(bitmap, maxW, maxH).ConfigureAwait(false);

                var folder = await EnsureFolderAsync("debug-artifacts\\screenshots").ConfigureAwait(false);
                var storageFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                using var fileStream = await storageFile.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fileStream);
                var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                encoder.SetSoftwareBitmap(bgra);
                await encoder.FlushAsync();

                var response = new CaptureViewResponse
                {
                    Success = true,
                    FilePath = storageFile.Path,
                    Width = bitmap.PixelWidth,
                    Height = bitmap.PixelHeight
                };
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        private async Task<string> CaptureRegionAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<CaptureRegionRequest>(argsJson)
                          ?? new CaptureRegionRequest();
                int maxW = req.MaxWidth ?? DefaultMaxDimension;
                int maxH = req.MaxHeight ?? DefaultMaxDimension;

                // Clamp normalized coordinates to [0, 1]
                double nx = Math.Max(0, Math.Min(1, req.X));
                double ny = Math.Max(0, Math.Min(1, req.Y));
                double nw = Math.Max(0, Math.Min(1 - nx, req.Width));
                double nh = Math.Max(0, Math.Min(1 - ny, req.Height));

                var fileName = $"region_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";

                SoftwareBitmap? bitmap = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(async () =>
                    {
                        bitmap = await CaptureUiAsBitmapAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    bitmap = await CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }

                if (bitmap == null)
                    throw new InvalidOperationException("Capture returned no frame.");

                int fullW = bitmap.PixelWidth;
                int fullH = bitmap.PixelHeight;

                // Convert normalized rect to pixel rect
                int px = (int)(nx * fullW);
                int py = (int)(ny * fullH);
                int pw = Math.Max(1, (int)(nw * fullW));
                int ph = Math.Max(1, (int)(nh * fullH));

                // Clamp to bitmap bounds
                px = Math.Min(px, fullW - 1);
                py = Math.Min(py, fullH - 1);
                pw = Math.Min(pw, fullW - px);
                ph = Math.Min(ph, fullH - py);

                // Crop the bitmap
                var cropped = CropBitmap(bitmap, px, py, pw, ph);

                // Resize the cropped region if needed
                int originalCropW = pw;
                int originalCropH = ph;
                cropped = await ResizeBitmapAsync(cropped, maxW, maxH).ConfigureAwait(false);
                int resizedCropW = cropped.PixelWidth;
                int resizedCropH = cropped.PixelHeight;

                // Scale fullWidth/fullHeight by the same factor so the coordinate
                // conversion formula (normalizedX = region.x + px / fullWidth) stays valid
                double scaleX = (double)resizedCropW / originalCropW;
                double scaleY = (double)resizedCropH / originalCropH;
                int scaledFullW = (int)(fullW * scaleX);
                int scaledFullH = (int)(fullH * scaleY);

                var folder = await EnsureFolderAsync("debug-artifacts\\screenshots").ConfigureAwait(false);
                var storageFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                using var fileStream = await storageFile.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fileStream);
                var bgra = SoftwareBitmap.Convert(cropped, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                encoder.SetSoftwareBitmap(bgra);
                await encoder.FlushAsync();

                var response = new CaptureRegionResponse
                {
                    Success = true,
                    FilePath = storageFile.Path,
                    RegionWidth = resizedCropW,
                    RegionHeight = resizedCropH,
                    FullWidth = scaledFullW,
                    FullHeight = scaledFullH,
                    NormalizedRegion = new NormalizedRect { X = nx, Y = ny, Width = nw, Height = nh }
                };
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        private async Task<string> ValidatePositionAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<ValidatePositionRequest>(argsJson)
                          ?? new ValidatePositionRequest();
                int maxW = req.MaxWidth ?? DefaultMaxDimension;
                int maxH = req.MaxHeight ?? DefaultMaxDimension;

                double nx = Math.Max(0, Math.Min(1, req.X));
                double ny = Math.Max(0, Math.Min(1, req.Y));

                var fileName = $"validate_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";

                SoftwareBitmap? bitmap = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(async () =>
                    {
                        bitmap = await CaptureUiAsBitmapAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    bitmap = await CaptureUiAsBitmapAsync().ConfigureAwait(false);
                }

                if (bitmap == null)
                    throw new InvalidOperationException("Capture returned no frame.");

                int w = bitmap.PixelWidth;
                int h = bitmap.PixelHeight;
                int cx = (int)(nx * w);
                int cy = (int)(ny * h);

                // Draw crosshair on the bitmap
                var annotated = DrawCrosshair(bitmap, cx, cy);

                // Resize after drawing the crosshair
                annotated = await ResizeBitmapAsync(annotated, maxW, maxH).ConfigureAwait(false);

                var folder = await EnsureFolderAsync("debug-artifacts\\screenshots").ConfigureAwait(false);
                var storageFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                using var fileStream = await storageFile.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fileStream);
                encoder.SetSoftwareBitmap(annotated);
                await encoder.FlushAsync();

                var response = new ValidatePositionResponse
                {
                    Success = true,
                    FilePath = storageFile.Path,
                    Width = annotated.PixelWidth,
                    Height = annotated.PixelHeight,
                    MarkerX = nx,
                    MarkerY = ny
                };
                return JsonConvert.SerializeObject(response);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Draws a high-visibility crosshair at the given pixel coordinates.
        /// Uses a thick black outline with a bright cyan center so the marker
        /// is visible on any background color.
        /// </summary>
        private static SoftwareBitmap DrawCrosshair(SoftwareBitmap source, int cx, int cy)
        {
            var bgra = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            var pixels = new byte[4 * w * h];
            bgra.CopyToBuffer(pixels.AsBuffer());

            const int armLength = 30;   // crosshair arm length in pixels
            const int outerThick = 5;   // black outline half-thickness
            const int innerThick = 2;   // cyan center half-thickness
            const int circleRadius = 10;

            // BGRA colors
            byte[] black = { 0, 0, 0, 255 };         // B,G,R,A
            byte[] cyan  = { 255, 255, 0, 255 };     // B=255,G=255,R=0 = cyan

            // Helper to set a pixel (bounds-checked)
            void SetPixel(int px, int py, byte[] color)
            {
                if (px < 0 || px >= w || py < 0 || py >= h) return;
                int idx = (py * w + px) * 4;
                pixels[idx]     = color[0]; // B
                pixels[idx + 1] = color[1]; // G
                pixels[idx + 2] = color[2]; // R
                pixels[idx + 3] = color[3]; // A
            }

            // Draw a filled rectangle helper
            void FillRect(int rx, int ry, int rw, int rh, byte[] color)
            {
                for (int dy = 0; dy < rh; dy++)
                    for (int dx = 0; dx < rw; dx++)
                        SetPixel(rx + dx, ry + dy, color);
            }

            // Horizontal arm — black outline
            FillRect(cx - armLength, cy - outerThick, armLength * 2 + 1, outerThick * 2 + 1, black);
            // Horizontal arm — cyan center
            FillRect(cx - armLength, cy - innerThick, armLength * 2 + 1, innerThick * 2 + 1, cyan);

            // Vertical arm — black outline
            FillRect(cx - outerThick, cy - armLength, outerThick * 2 + 1, armLength * 2 + 1, black);
            // Vertical arm — cyan center
            FillRect(cx - innerThick, cy - armLength, innerThick * 2 + 1, armLength * 2 + 1, cyan);

            // Circle outline around center — draw using midpoint circle algorithm
            for (int r = circleRadius - outerThick; r <= circleRadius + outerThick; r++)
                DrawCirclePixels(r, cx, cy, black, SetPixel);
            for (int r = circleRadius - innerThick; r <= circleRadius + innerThick; r++)
                DrawCirclePixels(r, cx, cy, cyan, SetPixel);

            // Center dot (bright magenta for maximum contrast)
            byte[] magenta = { 255, 0, 255, 255 }; // B=255,G=0,R=255 = magenta
            FillRect(cx - 2, cy - 2, 5, 5, magenta);

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());
            return result;
        }

        private static void DrawCirclePixels(int radius, int cx, int cy, byte[] color,
            Action<int, int, byte[]> setPixel)
        {
            if (radius <= 0) return;
            int x = radius, y = 0;
            int d = 1 - radius;
            while (x >= y)
            {
                setPixel(cx + x, cy + y, color);
                setPixel(cx - x, cy + y, color);
                setPixel(cx + x, cy - y, color);
                setPixel(cx - x, cy - y, color);
                setPixel(cx + y, cy + x, color);
                setPixel(cx - y, cy + x, color);
                setPixel(cx + y, cy - x, color);
                setPixel(cx - y, cy - x, color);
                y++;
                if (d <= 0)
                    d += 2 * y + 1;
                else
                {
                    x--;
                    d += 2 * (y - x) + 1;
                }
            }
        }

        private static SoftwareBitmap CropBitmap(SoftwareBitmap source, int x, int y, int width, int height)
        {
            // Convert to Bgra8 for consistent pixel access
            var bgra = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var srcBytes = new byte[4 * bgra.PixelWidth * bgra.PixelHeight];
            bgra.CopyToBuffer(srcBytes.AsBuffer());

            var dstBytes = new byte[4 * width * height];
            int srcStride = 4 * bgra.PixelWidth;
            int dstStride = 4 * width;

            for (int row = 0; row < height; row++)
            {
                Array.Copy(srcBytes, (y + row) * srcStride + x * 4, dstBytes, row * dstStride, dstStride);
            }

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(dstBytes.AsBuffer());
            return result;
        }

        /// <summary>
        /// Scales a bitmap down proportionally so that neither dimension exceeds the specified limits.
        /// Returns the original bitmap unchanged if it already fits within the constraints.
        /// </summary>
        private static async Task<SoftwareBitmap> ResizeBitmapAsync(SoftwareBitmap source, int maxWidth, int maxHeight)
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;

            if (w <= maxWidth && h <= maxHeight)
                return source;

            double scale = Math.Min((double)maxWidth / w, (double)maxHeight / h);
            uint newW = (uint)Math.Max(1, (int)(w * scale));
            uint newH = (uint)Math.Max(1, (int)(h * scale));

            // Encode → decode with BitmapTransform for high-quality resize
            using var memStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
            encoder.SetSoftwareBitmap(
                SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
            await encoder.FlushAsync();

            memStream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(memStream);
            var transform = new BitmapTransform
            {
                ScaledWidth = newW,
                ScaledHeight = newH,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var pixels = pixelData.DetachPixelData();

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, (int)newW, (int)newH, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());
            return result;
        }

        /// <summary>
        /// Renders the current XAML UI tree to a <see cref="SoftwareBitmap"/>.
        /// Must be called from the UI thread.
        /// </summary>
        private static async Task<SoftwareBitmap> CaptureUiAsBitmapAsync()
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Window.Current.Content);
            var pixels = await rtb.GetPixelsAsync();
            return SoftwareBitmap.CreateCopyFromBuffer(
                pixels,
                BitmapPixelFormat.Bgra8,
                rtb.PixelWidth,
                rtb.PixelHeight,
                BitmapAlphaMode.Premultiplied);
        }

        private static async Task<StorageFolder> EnsureFolderAsync(string relativePath)
        {
            var root = ApplicationData.Current.LocalFolder;
            StorageFolder folder = root;
            foreach (var part in relativePath.Split('\\', '/'))
                folder = await folder.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists);
            return folder;
        }
    }
}

