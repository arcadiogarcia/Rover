namespace Rover.Core.Coordinates
{
    public enum CoordinateSpace
    {
        /// <summary>Screen pixels.</summary>
        Absolute,

        /// <summary>0..1 range relative to the app window dimensions.</summary>
        Normalized,

        /// <summary>App window-relative pixels.</summary>
        Client
    }
}
