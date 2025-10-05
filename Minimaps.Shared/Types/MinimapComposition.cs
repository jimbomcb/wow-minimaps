﻿using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

public readonly record struct CompositionExtents(TileCoord Min, TileCoord Max);
public record class CompositionLOD(ImmutableDictionary<TileCoord, ContentHash> Tiles);

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

    private ImmutableDictionary<int, CompositionLOD> _lods;
    private readonly HashSet<TileCoord> _missingTiles;
    private byte[]? _hash;

    public MinimapComposition(ImmutableDictionary<int, CompositionLOD> lods, IReadOnlySet<TileCoord> missingTiles)
    {
        Debug.Assert(lods.ContainsKey(0), "Composition must contain LOD 0");

        _lods = lods;
        _missingTiles = [.. missingTiles];
    }

    public byte[] Hash => _hash ??= CalculateHash();

    public ImmutableDictionary<int, CompositionLOD> LODs => _lods;
    public IReadOnlyCollection<TileCoord> MissingTiles => _missingTiles;

    public int CountTiles() 
    {
        Debug.Assert(_lods.ContainsKey(0));
        return _missingTiles.Count + _lods[0].Tiles.Count;
    }

    public CompositionExtents? CalcExtents()
    {
        if (!_lods.TryGetValue(0, out var lod0) || lod0.Tiles.Count == 0)
            return null;

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        foreach (var coord in lod0.Tiles.Keys)
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
    /// - For each LOD, for each tile, in ascending x,y order, write the tile X, tile Y, and hash bytes
    /// - Write the X,Y of each "missing" tile in ascending x,y order
    /// The hash bytes are returned as a lowercase hex string.
    /// </summary>
    private byte[] CalculateHash()
    {
        Debug.Assert(_lods.ContainsKey(0), "Composition must contain LOD 0");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Span<byte> hash = stackalloc byte[16];

        for (int lod = 0; lod <= MAX_LOD; lod++)
        {
            if (!_lods.TryGetValue(lod, out var lodN))
                continue;

            writer.Write(lod);

            foreach (var entry in lodN.Tiles.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
            {
                writer.Write(entry.Key.X);
                writer.Write(entry.Key.Y);
                entry.Value.CopyTo(hash);
                writer.Write(hash);
            }
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
