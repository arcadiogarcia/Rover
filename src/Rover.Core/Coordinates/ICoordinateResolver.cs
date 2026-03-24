namespace Rover.Core.Coordinates
{
    public interface ICoordinateResolver
    {
        CoordinatePoint Resolve(CoordinatePoint point, CoordinateSpace space);
    }
}
