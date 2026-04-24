using System.Collections.Generic;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace zRover.Core.Tools.Screenshot
{
    /// <summary>
    /// Per-frame metadata for a captured compositor frame. PNGs are written to
    /// <see cref="CaptureFramesResponse.Directory"/>; <see cref="Path"/> is the
    /// absolute path to the PNG on disk.
    /// </summary>
    public sealed class CaptureFrameInfo
    {
#if !WINDOWS_UWP
        [JsonPropertyName("index")]
#endif
        [JsonProperty("index")]
        public int Index { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("path")]
#endif
        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Compositor system-relative timestamp in microseconds (from
        /// <c>Direct3D11CaptureFrame.SystemRelativeTime.Ticks</c>).
        /// Use this to compute exact inter-frame intervals.
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("systemRelativeTimeUs")]
#endif
        [JsonProperty("systemRelativeTimeUs")]
        public long SystemRelativeTimeUs { get; set; }

        /// <summary>
        /// Milliseconds elapsed since the previous captured frame.
        /// 0 for the first frame.
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("deltaFromPreviousMs")]
#endif
        [JsonProperty("deltaFromPreviousMs")]
        public double DeltaFromPreviousMs { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("contentWidth")]
#endif
        [JsonProperty("contentWidth")]
        public int ContentWidth { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("contentHeight")]
#endif
        [JsonProperty("contentHeight")]
        public int ContentHeight { get; set; }
    }

    public sealed class CaptureFramesResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("error")]
#endif
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("sessionId")]
#endif
        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// Absolute filesystem directory containing the captured PNG frames.
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("directory")]
#endif
        [JsonProperty("directory")]
        public string Directory { get; set; } = string.Empty;

#if !WINDOWS_UWP
        [JsonPropertyName("requestedFrameCount")]
#endif
        [JsonProperty("requestedFrameCount")]
        public int RequestedFrameCount { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("capturedFrameCount")]
#endif
        [JsonProperty("capturedFrameCount")]
        public int CapturedFrameCount { get; set; }

        /// <summary>
        /// Total wall-clock duration of the capture session in milliseconds.
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("totalDurationMs")]
#endif
        [JsonProperty("totalDurationMs")]
        public double TotalDurationMs { get; set; }

        /// <summary>
        /// Average interval between consecutive captured frames in milliseconds
        /// (computed from compositor timestamps, not wall-clock).
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("averageFrameIntervalMs")]
#endif
        [JsonProperty("averageFrameIntervalMs")]
        public double AverageFrameIntervalMs { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("averageFps")]
#endif
        [JsonProperty("averageFps")]
        public double AverageFps { get; set; }

        /// <summary>
        /// Window content size in render pixels at the start of capture.
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("windowWidth")]
#endif
        [JsonProperty("windowWidth")]
        public int WindowWidth { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("windowHeight")]
#endif
        [JsonProperty("windowHeight")]
        public int WindowHeight { get; set; }

        /// <summary>
        /// Pixel size of the encoded PNG frames (after maxWidth/maxHeight downscale).
        /// </summary>
#if !WINDOWS_UWP
        [JsonPropertyName("bitmapWidth")]
#endif
        [JsonProperty("bitmapWidth")]
        public int BitmapWidth { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("bitmapHeight")]
#endif
        [JsonProperty("bitmapHeight")]
        public int BitmapHeight { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("frames")]
#endif
        [JsonProperty("frames")]
        public List<CaptureFrameInfo> Frames { get; set; } = new List<CaptureFrameInfo>();
    }
}
