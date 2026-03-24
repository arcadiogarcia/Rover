namespace Rover.Core
{
    public class DebugHostOptions
    {
        public string AppName { get; set; } = "App";
        public int Port { get; set; } = 7331;
        public bool EnableInputInjection { get; set; } = true;
        public bool EnableScreenshots { get; set; } = true;
        public bool RequireAuthToken { get; set; } = true;
        public string? AuthToken { get; set; }
        public string? ArtifactDirectory { get; set; }
        public bool SkipUwpCheck { get; set; } = false;
        public bool TestAppService { get; set; } = false;
    }
}
