using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

public readonly record struct CompositionExtents(TileCoord Min, TileCoord Max);

/// <summary>
/// A minimap composition describes the list of tile hashes and the location of each hash that make up a minimap.
/// In the database this is stored as a JSONB object mapping "X,Y" to "hash".
/// Hashed in a specific order to produce a unique hash for this specific layout and contents (regardless of JSON key order).
/// TODO: Composition LODing, composition thumbnail
/// </summary>
[JsonConverter(typeof(MinimapCompositionConverter))]
public class MinimapComposition(IReadOnlyDictionary<TileCoord, ContentHash> compositionEntry, IReadOnlySet<TileCoord> missingTiles) : IEquatable<MinimapComposition?>
{
    private readonly Dictionary<TileCoord, ContentHash> _composition = compositionEntry.ToDictionary();
    private readonly HashSet<TileCoord> _missingTiles = [.. missingTiles];
    private byte[]? _hash;
    public byte[] Hash => _hash ??= CalculateHash();
    public IReadOnlyDictionary<TileCoord, ContentHash> Composition => _composition;
    public IReadOnlyCollection<TileCoord> MissingTiles => _missingTiles;
    public int TotalTiles => _composition.Count + _missingTiles.Count;
    public CompositionExtents? CalcExtents()
    {
        if (_composition.Count == 0)
            return null;

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        foreach (var coord in _composition.Keys)
        {
            if (coord.X < minX) minX = coord.X;
            if (coord.Y < minY) minY = coord.Y;
            if (coord.X > maxX) maxX = coord.X;
            if (coord.Y > maxY) maxY = coord.Y;
        }
        return new(new(minX, minY), new(maxX, maxY));
    }

    /// <summary>
    /// Follows a specific order that will result in a consistent hash from the same tile hashes at the same coordates
    /// The hash of a stream of ASCII bytes of:
    /// - For each tile, in ascending x,y order, write the tile X, tile Y, and hash bytes
    /// - Write the X,Y of each "missing" tile in ascending x,y order
    /// The hash bytes are returned as a lowercase hex string.
    /// </summary>
    private byte[] CalculateHash()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Span<byte> hash = stackalloc byte[16];

        foreach (var entry in _composition.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
        {
            writer.Write(entry.Key.X);
            writer.Write(entry.Key.Y);

            entry.Value.CopyTo(hash);
            writer.Write(hash);
        }
        foreach (var missingTile in _missingTiles.OrderBy(x => x.X).ThenBy(x => x.Y))
        {
            writer.Write(missingTile.X);
            writer.Write(missingTile.Y);
        }
        writer.Flush();
        stream.Position = 0;
        return _hash = MD5.HashData(stream);
    }

    public override bool Equals(object? obj) => Equals(obj as MinimapComposition);
    public bool Equals(MinimapComposition? other) => other is not null && Hash == other.Hash;
    public override int GetHashCode() => Hash.GetHashCode();
    public static bool operator ==(MinimapComposition? left, MinimapComposition? right) => EqualityComparer<MinimapComposition>.Default.Equals(left, right);
    public static bool operator !=(MinimapComposition? left, MinimapComposition? right) => !(left == right);
}
