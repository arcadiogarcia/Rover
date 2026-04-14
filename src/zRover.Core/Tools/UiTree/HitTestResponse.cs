#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Tools.Screenshot;

namespace zRover.Core.Tools.UiTree
{
    public sealed class HitTestResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("type")]
#endif
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
        public string? Type { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("name")]
#endif
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string? Name { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("automationName")]
#endif
        [JsonProperty("automationName", NullValueHandling = NullValueHandling.Ignore)]
        public string? AutomationName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("text")]
#endif
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
        public string? Text { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("centerX")]
#endif
        [JsonProperty("centerX")]
        public double CenterX { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("centerY")]
#endif
        [JsonProperty("centerY")]
        public double CenterY { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("bounds")]
#endif
        [JsonProperty("bounds", NullValueHandling = NullValueHandling.Ignore)]
        public NormalizedRect? Bounds { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("isVisible")]
#endif
        [JsonProperty("isVisible")]
        public bool IsVisible { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("isEnabled")]
#endif
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("error")]
#endif
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }
    }
}
