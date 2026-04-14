using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using zRover.Core.Tools.Screenshot;

namespace zRover.Core.Tools.InputInjection
{
    public class ActivateElementResponse
    {
#if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("method")]
#endif
        [JsonProperty("method", NullValueHandling = NullValueHandling.Ignore)]
        public string? Method { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("elementName")]
#endif
        [JsonProperty("elementName", NullValueHandling = NullValueHandling.Ignore)]
        public string? ElementName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("elementType")]
#endif
        [JsonProperty("elementType", NullValueHandling = NullValueHandling.Ignore)]
        public string? ElementType { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("automationName")]
#endif
        [JsonProperty("automationName", NullValueHandling = NullValueHandling.Ignore)]
        public string? AutomationName { get; set; }

#if !WINDOWS_UWP
        [JsonPropertyName("bounds")]
#endif
        [JsonProperty("bounds", NullValueHandling = NullValueHandling.Ignore)]
        public NormalizedRect? Bounds { get; set; }

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
