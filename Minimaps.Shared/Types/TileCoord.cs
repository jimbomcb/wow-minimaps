namespace Minimaps.Shared.Types;

/// <summary>
/// Coordinate of a specific map tile, ranges from -32,+32 X/Y
/// </summary>
public readonly record struct TileCoord(int X, int Y);