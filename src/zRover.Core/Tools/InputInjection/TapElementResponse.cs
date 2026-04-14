using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Tools.Screenshot;

namespace zRover.Core.Tools.InputInjection
{
    public class TapElementResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("elementName")]
#endif
        [JsonProperty("elementName")]
        public string? ElementName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("elementType")]
#endif
        [JsonProperty("elementType")]
        public string? ElementType { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("automationName")]
#endif
        [JsonProperty("automationName")]
        public string? AutomationName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("bounds")]
#endif
        [JsonProperty("bounds")]
        public NormalizedRect? Bounds { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("tappedAt")]
#endif
        [JsonProperty("tappedAt")]
        public NormalizedRect? TappedAt { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("device")]
#endif
        [JsonProperty("device")]
        public string Device { get; set; } = "touch";

#if !WINDOWS_UWP
        [JsonPropertyName("dryRun")]
#endif
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("error")]
#endif
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}
