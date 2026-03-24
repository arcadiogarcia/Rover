using Windows.Graphics.Display;
using Windows.UI.ViewManagement;
using Rover.Core.Coordinates;

namespace Rover.Uwp.Coordinates
{
    internal sealed class UwpCoordinateResolver : ICoordinateResolver
    {
        public CoordinatePoint Resolve(CoordinatePoint point, CoordinateSpace space)
        {
            switch (space)
            {
                case CoordinateSpace.Absolute:
                    return point;

                case CoordinateSpace.Client:
                    // Client-relative pixels: offset by window position.
                    // For a full-screen UWP app the window origin is (0,0), so this
                    // is effectively the same as absolute.
                    return point;

                case CoordinateSpace.Normalized:
                default:
                    // 0..1 → screen pixels, accounting for the current display bounds
                    // and raw-pixel DPI scaling.
                    var bounds = ApplicationView.GetForCurrentView().VisibleBounds;
                    var displayInfo = DisplayInformation.GetForCurrentView();
                    double scaleX = displayInfo.RawPixelsPerViewPixel;
                    double scaleY = displayInfo.RawPixelsPerViewPixel;

                    double absX = (point.X * bounds.Width  + bounds.X) * scaleX;
                    double absY = (point.Y * bounds.Height + bounds.Y) * scaleY;
                    return new CoordinatePoint(absX, absY);
            }
        }
    }
}
