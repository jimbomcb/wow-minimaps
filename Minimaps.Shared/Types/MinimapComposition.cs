using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// A minimap composition describes the list of tile hashes and the location of each hash that make up a minimap.
/// In the database this is stored as a JSONB object mapping "X,Y" to "hash".
/// Hashed in a specific order to produce a unique hash for this specific layout and contents (regardless of JSON key order).
/// TODO: Composition LODing, composition thumbnail
/// </summary>
[JsonConverter(typeof(MinimapCompositionConverter))]
public class MinimapComposition(IReadOnlyDictionary<TileCoord, string> compositionEntry, IReadOnlySet<TileCoord> missingTiles) : IEquatable<MinimapComposition?>
{
    private readonly Dictionary<TileCoord, string> _composition = compositionEntry.ToDictionary();
    private readonly HashSet<TileCoord> _missingTiles = [.. missingTiles];
    private string? _hash;

    public string Hash => _hash ??= CalculateHash();
    public IReadOnlyDictionary<TileCoord, string> Composition => _composition;
    public IReadOnlyCollection<TileCoord> MissingTiles => _missingTiles;

    /// <summary>
    /// Follows a specific order that will result in a consistent hash from the same tile hashes at the same coordates
    /// The hash of a stream of ASCII bytes of:
    /// - For each tile, in ascending x,y order, write the tile X, tile Y, and ASCII-encoded tile hash string
    /// - Write the X,Y of each "missing" tile in ascending x,y order
    /// The hash bytes are returned as a lowercase hex string.
    /// </summary>
    private string CalculateHash()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var entry in _composition.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
        {
            writer.Write(entry.Key.X);
            writer.Write(entry.Key.Y);
            writer.Write(Encoding.ASCII.GetBytes(entry.Value));
        }
        foreach (var missingTile in _missingTiles.OrderBy(x => x.X).ThenBy(x => x.Y))
        {
            writer.Write(missingTile.X);
            writer.Write(missingTile.Y);
        }
        writer.Flush();
        stream.Position = 0;
        _hash = Convert.ToHexStringLower(MD5.HashData(stream));
        return _hash;
    }

    public override bool Equals(object? obj) => Equals(obj as MinimapComposition);
    public bool Equals(MinimapComposition? other) => other is not null && Hash == other.Hash;
    public override int GetHashCode() => Hash.GetHashCode();
    public static bool operator ==(MinimapComposition? left, MinimapComposition? right) => EqualityComparer<MinimapComposition>.Default.Equals(left, right);
    public static bool operator !=(MinimapComposition? left, MinimapComposition? right) => !(left == right);
}
