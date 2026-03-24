using System;
using System.IO;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Rover.Core;
using Rover.Core.Tools.Screenshot;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Rover.Uwp.Capabilities
{
    public sealed class ScreenshotCapability : IDebugCapability
    {
        

        private const string CaptureSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""format"": { ""type"": ""string"", ""enum"": [""png""], ""default"": ""png"" }
  }
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
                "Captures the current app window as a PNG screenshot and returns its file path and dimensions (in render pixels). To interact with elements in the screenshot, use inject_tap/inject_drag_path with normalized coordinates (0.0–1.0), not pixel values.",
                CaptureSchema,
                CaptureAsync);
        }

        private async Task<string> CaptureAsync(string argsJson)
        {
            try
            {
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

