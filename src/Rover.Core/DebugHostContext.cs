using System;
using System.Threading.Tasks;
using Rover.Core.Coordinates;

namespace Rover.Core
{
    public class DebugHostContext
    {
        public DebugHostOptions Options { get; }
        public ICoordinateResolver CoordinateResolver { get; }
        public string ArtifactDirectory { get; }

        /// <summary>
        /// Schedules async work on the app UI thread and awaits its completion.
        /// Null if not provided. Used by capabilities that require UI thread access.
        /// </summary>
        public Func<Func<Task>, Task>? RunOnUiThread { get; }

        public DebugHostContext(
            DebugHostOptions options,
            ICoordinateResolver coordinateResolver,
            string artifactDirectory,
            Func<Func<Task>, Task>? runOnUiThread = null)
        {
            Options = options;
            CoordinateResolver = coordinateResolver;
            ArtifactDirectory = artifactDirectory;
            RunOnUiThread = runOnUiThread;
        }
    }
}
