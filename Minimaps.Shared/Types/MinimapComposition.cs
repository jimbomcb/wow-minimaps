using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// A minimap composition describes the list of tile hashes and the location of each hash that make up a minimap.
/// In the database this is stored as a JSONB object mapping "X,Y" to "hash".
/// </summary>
[JsonConverter(typeof(MinimapCompositionConverter))]
public class MinimapComposition(IReadOnlyDictionary<TileCoord, string> compositionEntry)
{
    private Dictionary<TileCoord, string> _composition = compositionEntry.ToDictionary();
    private string? _hash;

    public string Hash => _hash ??= CalculateHash();
    public IReadOnlyDictionary<TileCoord, string> Composition => _composition;

    /// <summary>
    /// Follows a specific order that will result in a consistent hash from the same tile hashes at the same coordates
    /// The hash of a stream of ASCII bytes of:
    /// - For each tile, in ascending x,y order, write the tile X, tile Y, and ASCII-encoded tile hash string
    /// The hash bytes are returned as a lowercase hex string.
    /// </summary>
    private string CalculateHash()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var orderedComposition = _composition.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y);
        foreach (var entry in orderedComposition)
        {
            writer.Write(entry.Key.X);
            writer.Write(entry.Key.Y);
            writer.Write(Encoding.ASCII.GetBytes(entry.Value));
        }
        writer.Flush();
        stream.Position = 0;
        _hash = Convert.ToHexStringLower(MD5.HashData(stream));
        return _hash;
    }
}
