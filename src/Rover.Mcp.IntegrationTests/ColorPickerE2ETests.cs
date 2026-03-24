using System.Drawing;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Rover.Mcp.IntegrationTests;

/// <summary>
/// E2E tests that exercise MCP input injection and screenshot capabilities
/// against the Color Picker test UI in the deployed UWP app.
///
/// Each test:
///   1. Injects taps on color preset buttons or drags on RGB sliders
///   2. Captures a screenshot via the MCP capture_current_view tool
///   3. Reads the resulting PNG and samples pixel color in the preview area
///   4. Asserts the pixel color matches the expected value
///
/// This validates the complete round-trip:
///   Test → MCP HTTP → FullTrust Server → AppService IPC →
///   UWP InputInjector → XAML UI update → RenderTargetBitmap →
///   PNG file → pixel verification
///
/// The Color Picker page uses a 10-star proportional grid so normalized
/// coordinates map to predictable UI regions regardless of window size.
/// </summary>
[Collection("E2E")]
public class ColorPickerE2ETests : IAsyncLifetime
{
    private static readonly Uri McpEndpoint = new(
        Environment.GetEnvironmentVariable("ROVER_MCP_ENDPOINT")
        ?? "http://localhost:5100/mcp");

    private McpClient _client = null!;

    // ═══════════════════════════════════════════════════════════════
    //  Layout constants (normalized coordinates matching the XAML grid)
    //
    //  Row heights:  1* / 1* / 1* / 1* / 1* / 4* / 1*  (total 10*)
    //  Button cols:  5 equal columns → centers at 0.1, 0.3, 0.5, 0.7, 0.9
    // ═══════════════════════════════════════════════════════════════

    // Y center of each row
    const double TitleY     = 0.05;
    const double ButtonY    = 0.15;
    const double RSliderY   = 0.25;
    const double GSliderY   = 0.35;
    const double BSliderY   = 0.45;
    const double PreviewY   = 0.70;   // center of the 4* preview row
    const double HexLabelY  = 0.95;

    // X center of each button column
    const double RedBtnX    = 0.10;
    const double GreenBtnX  = 0.30;
    const double BlueBtnX   = 0.50;
    const double YellowBtnX = 0.70;
    const double WhiteBtnX  = 0.90;

    // Slider track X range (label=30px + padding=20px on left, value=40px + padding=20px on right)
    const double SliderLeftX  = 0.10;
    const double SliderRightX = 0.88;

    // Where to sample the preview color
    const double SampleX = 0.50;
    const double SampleY = PreviewY;

    // Color channel tolerance (0-255)
    const byte HiThreshold = 200;
    const byte LoThreshold = 55;

    public async Task InitializeAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = McpEndpoint
        });
        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (_client is IAsyncDisposable d) await d.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task TapAtAsync(double x, double y)
    {
        var result = await _client.CallToolAsync("inject_tap",
            new Dictionary<string, object?>
            {
                { "x", x }, { "y", y }, { "coordinateSpace", "normalized" }
            });
        result.IsError.Should().NotBe(true, $"inject_tap({x:F2},{y:F2}) failed");
        await Task.Delay(600);
    }

    private async Task DragAsync(double x1, double y1, double x2, double y2, int durationMs = 400)
    {
        var result = await _client.CallToolAsync("inject_drag_path",
            new Dictionary<string, object?>
            {
                { "points", new[]
                    {
                        new Dictionary<string, object?> { { "x", x1 }, { "y", y1 } },
                        new Dictionary<string, object?> { { "x", x2 }, { "y", y2 } }
                    }
                },
                { "durationMs", durationMs },
                { "coordinateSpace", "normalized" }
            });
        result.IsError.Should().NotBe(true, "inject_drag_path failed");
        await Task.Delay(600);
    }

    private async Task<(string filePath, int width, int height)> CaptureScreenshotAsync()
    {
        var result = await _client.CallToolAsync("capture_current_view",
            new Dictionary<string, object?> { { "format", "png" } });
        result.IsError.Should().NotBe(true, "capture_current_view failed");

        var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        textBlock.Should().NotBeNull("capture result should contain text");

        using var doc = JsonDocument.Parse(textBlock!.Text);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue("screenshot capture should succeed");

        return (
            root.GetProperty("filePath").GetString()!,
            root.GetProperty("width").GetInt32(),
            root.GetProperty("height").GetInt32()
        );
    }

    /// <summary>
    /// Reads a pixel at the given normalized position from a PNG file.
    /// Uses FileShare.ReadWrite in case the UWP app still has the file open.
    /// </summary>
    private static Color ReadPixelAt(string pngPath, double normX, double normY)
    {
        using var stream = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bitmap = new Bitmap(stream);
        int x = Math.Clamp((int)(normX * bitmap.Width), 0, bitmap.Width - 1);
        int y = Math.Clamp((int)(normY * bitmap.Height), 0, bitmap.Height - 1);
        return bitmap.GetPixel(x, y);
    }

    private static Color SamplePreview(string pngPath) => ReadPixelAt(pngPath, SampleX, SampleY);

    private static void AssertColorApprox(Color actual, byte expectR, byte expectG, byte expectB, string because)
    {
        actual.R.Should().BeCloseTo(expectR, LoThreshold, $"R – {because}");
        actual.G.Should().BeCloseTo(expectG, LoThreshold, $"G – {because}");
        actual.B.Should().BeCloseTo(expectB, LoThreshold, $"B – {because}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Button tap → screenshot → pixel color verification
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TapRedButton_PreviewBecomesRed()
    {
        await TapAtAsync(RedBtnX, ButtonY);
        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.R.Should().BeGreaterThan(HiThreshold, "preview should be red");
        c.G.Should().BeLessThan(LoThreshold);
        c.B.Should().BeLessThan(LoThreshold);
    }

    [Fact]
    public async Task TapGreenButton_PreviewBecomesGreen()
    {
        await TapAtAsync(GreenBtnX, ButtonY);
        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.R.Should().BeLessThan(LoThreshold);
        c.G.Should().BeGreaterThan(HiThreshold, "preview should be green");
        c.B.Should().BeLessThan(LoThreshold);
    }

    [Fact]
    public async Task TapBlueButton_PreviewBecomesBlue()
    {
        await TapAtAsync(BlueBtnX, ButtonY);
        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.R.Should().BeLessThan(LoThreshold);
        c.G.Should().BeLessThan(LoThreshold);
        c.B.Should().BeGreaterThan(HiThreshold, "preview should be blue");
    }

    [Fact]
    public async Task TapYellowButton_PreviewBecomesYellow()
    {
        await TapAtAsync(YellowBtnX, ButtonY);
        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.R.Should().BeGreaterThan(HiThreshold, "yellow has high red");
        c.G.Should().BeGreaterThan(HiThreshold, "yellow has high green");
        c.B.Should().BeLessThan(LoThreshold, "yellow has no blue");
    }

    [Fact]
    public async Task TapWhiteButton_PreviewBecomesWhite()
    {
        await TapAtAsync(WhiteBtnX, ButtonY);
        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.R.Should().BeGreaterThan(HiThreshold, "white has high red");
        c.G.Should().BeGreaterThan(HiThreshold, "white has high green");
        c.B.Should().BeGreaterThan(HiThreshold, "white has high blue");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Slider drag → screenshot → pixel color verification
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DragRedSlider_IncreasesRedComponent()
    {
        // Start at pure blue so R slider is at 0
        await TapAtAsync(BlueBtnX, ButtonY);

        // Drag R slider from left to right
        await DragAsync(SliderLeftX, RSliderY, SliderRightX, RSliderY, 500);

        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.R.Should().BeGreaterThan(100, "dragging R slider right should add red");
        c.B.Should().BeGreaterThan(100, "blue should still be present from preset");
    }

    [Fact]
    public async Task DragGreenSlider_IncreasesGreenComponent()
    {
        // Start at pure red so G slider is at 0
        await TapAtAsync(RedBtnX, ButtonY);

        // Drag G slider from left to right
        await DragAsync(SliderLeftX, GSliderY, SliderRightX, GSliderY, 500);

        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.G.Should().BeGreaterThan(100, "dragging G slider right should add green");
        c.R.Should().BeGreaterThan(100, "red should still be present from preset");
    }

    [Fact]
    public async Task DragBlueSlider_IncreasesBlueComponent()
    {
        // Start at pure red so B slider is at 0
        await TapAtAsync(RedBtnX, ButtonY);

        // Drag B slider from left to right
        await DragAsync(SliderLeftX, BSliderY, SliderRightX, BSliderY, 500);

        var (path, _, _) = await CaptureScreenshotAsync();
        var c = SamplePreview(path);

        c.B.Should().BeGreaterThan(100, "dragging B slider right should add blue");
        c.R.Should().BeGreaterThan(100, "red should still be present from preset");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multi-step sequential test
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SequentialColorChanges_PreviewUpdatesEachTime()
    {
        // 1. Tap Red → verify red
        await TapAtAsync(RedBtnX, ButtonY);
        var (p1, _, _) = await CaptureScreenshotAsync();
        var c1 = SamplePreview(p1);
        c1.R.Should().BeGreaterThan(HiThreshold, "step 1: should be red");
        c1.G.Should().BeLessThan(LoThreshold);

        // 2. Tap Blue → verify blue (red should be gone)
        await TapAtAsync(BlueBtnX, ButtonY);
        var (p2, _, _) = await CaptureScreenshotAsync();
        var c2 = SamplePreview(p2);
        c2.B.Should().BeGreaterThan(HiThreshold, "step 2: should be blue");
        c2.R.Should().BeLessThan(LoThreshold, "step 2: red should be gone");

        // 3. Tap Green → verify green (blue should be gone)
        await TapAtAsync(GreenBtnX, ButtonY);
        var (p3, _, _) = await CaptureScreenshotAsync();
        var c3 = SamplePreview(p3);
        c3.G.Should().BeGreaterThan(HiThreshold, "step 3: should be green");
        c3.B.Should().BeLessThan(LoThreshold, "step 3: blue should be gone");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Screenshot captures actual UI dimensions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Screenshot_HasReasonableDimensions()
    {
        var (path, width, height) = await CaptureScreenshotAsync();

        width.Should().BeGreaterThan(100, "screenshot should have reasonable width");
        height.Should().BeGreaterThan(100, "screenshot should have reasonable height");
        File.Exists(path).Should().BeTrue("screenshot PNG file should exist at returned path");
    }
}
