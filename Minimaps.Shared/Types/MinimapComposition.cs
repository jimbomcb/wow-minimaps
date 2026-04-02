using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

public readonly record struct CompositionExtents(TileCoord Min, TileCoord Max);
public record class CompositionLOD(Dictionary<TileCoord, ContentHash> Tiles);

/// <summary>
/// A minimap composition describes the list of tile hashes and the location of each hash that make up a minimap.
/// In the database this is stored as a JSONB object mapping "X,Y" to "hash".
/// Hashed in a specific order to produce a unique hash for this specific layout and contents (regardless of JSON key order).
/// LOD0 is base tile map, LODn is 2^n downsampled version of LOD0.
/// </summary>
[JsonConverter(typeof(MinimapCompositionConverter))]
public class MinimapComposition : IEquatable<MinimapComposition?>
{
    public const int MAX_LOD = 6; // LOD6: 2^6 = 64: single tile

    private readonly CompositionLOD?[] _lods;
    private readonly HashSet<TileCoord> _missingTiles;
    private byte[]? _hash;

    public byte[] Hash => _hash ??= CalculateHash();
    public IReadOnlyCollection<TileCoord> MissingTiles => _missingTiles;
    public int TileSize { get; set; } = -1;

    public MinimapComposition(IReadOnlyDictionary<int, CompositionLOD> lods, IReadOnlySet<TileCoord> missingTiles)
    {
        Debug.Assert(lods.ContainsKey(0), "Composition must contain LOD 0");

        _lods = new CompositionLOD?[MAX_LOD + 1];
        foreach (var kvp in lods)
        {
            _lods[kvp.Key] = kvp.Value;
        }
        _missingTiles = [.. missingTiles];
    }

    // LOD0-only (aka no LODs) composition
    public MinimapComposition(Dictionary<TileCoord, ContentHash> lod0Tiles, IReadOnlySet<TileCoord> missingTiles)
    {
        _lods = new CompositionLOD?[MAX_LOD + 1];
        _lods[0] = new CompositionLOD(lod0Tiles);
        _missingTiles = [.. missingTiles];
    }

    public CompositionLOD? GetLOD(int level)
    {
        if (level < 0 || level > MAX_LOD) return null;
        return _lods[level];
    }

    public int CountTiles()
    {
        Debug.Assert(_lods[0] != null);
        return _missingTiles.Count + _lods[0]!.Tiles.Count;
    }

    public CompositionExtents? CalcExtents()
    {
        var lod0 = _lods[0];
        if ((lod0?.Tiles.Count ?? 0) == 0 && _missingTiles.Count == 0)
            return null;

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        if (lod0 != null)
        {
            foreach (var coord in lod0.Tiles.Keys)
            {
                if (coord.X < minX) minX = coord.X;
                if (coord.Y < minY) minY = coord.Y;
                if (coord.X > maxX) maxX = coord.X;
                if (coord.Y > maxY) maxY = coord.Y;
            }
        }

        foreach (var coord in _missingTiles)
        {
            if (coord.X < minX) minX = coord.X;
            if (coord.Y < minY) minY = coord.Y;
            if (coord.X > maxX) maxX = coord.X;
            if (coord.Y > maxY) maxY = coord.Y;
        }

        return new(new(minX, minY), new(maxX, maxY));
    }

    /// <summary>
    /// Deterministic hash from LOD0 tiles + missing tiles only. LOD1-6 are derivable from LOD0 alone.
    /// - For each LOD0 tile in ascending x,y order: write tile X, tile Y, and 16-byte hash.
    /// - Then the X,Y of each missing tile in ascending x,y order.
    /// Missing tiles are referenced by the WDT but the ADT FDID is not part of the build at all (not just unavailable from the CDN).
    /// </summary>
    private byte[] CalculateHash()
    {
        Debug.Assert(_lods[0] != null, "Composition must contain LOD 0");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Span<byte> hash = stackalloc byte[16];

        foreach (var entry in _lods[0]!.Tiles.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
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
    public bool Equals(MinimapComposition? other) => other is not null && Hash.SequenceEqual(other.Hash);
    public override int GetHashCode() => Hash.GetHashCode();
    public static bool operator ==(MinimapComposition? left, MinimapComposition? right) => EqualityComparer<MinimapComposition>.Default.Equals(left, right);
    public static bool operator !=(MinimapComposition? left, MinimapComposition? right) => !(left == right);
}