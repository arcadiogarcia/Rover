#if !WINDOWS_UWP
using System.Text.Json.Serialization;
#endif
using Newtonsoft.Json;

namespace Rover.Core.Tools.Screenshot
{
    public sealed class CaptureViewRequest
    {
        #if !WINDOWS_UWP
        [JsonPropertyName("format")]
#endif
        [JsonProperty("format")]
        public string Format { get; set; } = "png";
    }
}



