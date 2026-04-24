#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    /// <summary>
    /// Request payload for the <c>capture_frames</c> tool. Captures a burst of
    /// compositor frames (one per vblank that produces a present) using the
    /// Windows.Graphics.Capture API for diagnosing animations and flicker.
    /// </summary>
    public sealed class CaptureFramesRequest
    {
#if !WINDOWS_UWP
        [JsonPropertyName("frameCount")]
#endif
        [JsonProperty("frameCount")]
        public int? FrameCount { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("maxDurationMs")]
#endif
        [JsonProperty("maxDurationMs")]
        public int? MaxDurationMs { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("maxWidth")]
#endif
        [JsonProperty("maxWidth")]
        public int? MaxWidth { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("maxHeight")]
#endif
        [JsonProperty("maxHeight")]
        public int? MaxHeight { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("includeCursor")]
#endif
        [JsonProperty("includeCursor")]
        public bool? IncludeCursor { get; set; }
    }
}
