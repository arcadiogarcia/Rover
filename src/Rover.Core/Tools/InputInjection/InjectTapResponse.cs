using System;
#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;
using Rover.Core.Coordinates;

namespace Rover.Core.Tools.InputInjection
{
    public class InjectTapResponse
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("success")]
#endif
        [JsonProperty("success")]
        public bool Success { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("resolvedCoordinates")]
#endif
        [JsonProperty("resolvedCoordinates")]
        public CoordinatePoint? ResolvedCoordinates { get; set; }

        #if !WINDOWS_UWP
        [JsonPropertyName("device")]
#endif
        [JsonProperty("device")]
        public string Device { get; set; } = "touch";

        #if !WINDOWS_UWP
        [JsonPropertyName("timestamp")]
#endif
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }
}



