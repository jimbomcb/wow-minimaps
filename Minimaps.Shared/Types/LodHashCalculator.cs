using System.Security.Cryptography;

namespace Minimaps.Shared.Types;

/// <summary>
/// Computes LOD tile hashes from LOD0 tile data.
/// Each LOD0 tile is the MD5 hash of the BLP data (also known as the ContentKey in blizz land)
/// LOD1+ tile hash = MD5 of concatenated child hashes (16 bytes each), where:
/// - Present tiles use their actual content hash
/// - Build-missing tiles (not in the build at all) use all-zero bytes (0x00 x 16)
/// - CDN-missing tiles (content key known but not found on any CDN) use CdnMissingSentinel (0x67 x 16)
/// Don't go changing this without the corresponding ts logic (like BuildVersions etc)
/// </summary>
public static class LodHashCalculator
{
    /// <summary>
    /// Compute LOD tile hashes for LOD levels 1 through maxLod.
    /// </summary>
    /// <returns>Dictionary of (lodLevel, coord) to computed LOD hash</returns>
    public static Dictionary<(int Level, TileCoord Coord), ContentHash> ComputeLodHashes(
        IReadOnlyDictionary<TileCoord, ContentHash> lod0Tiles,
        IReadOnlySet<ContentHash>? cdnMissing, 
        int maxLod = MinimapComposition.MAX_LOD)
    {
        var result = new Dictionary<(int Level, TileCoord Coord), ContentHash>();
        Span<byte> hashBytes = stackalloc byte[16];

        for (int level = 1; level <= maxLod; level++)
        {
            int factor = 1 << level;
            for (int lodX = 0; lodX < 64; lodX += factor)
            {
                for (int lodY = 0; lodY < 64; lodY += factor)
                {
                    bool hasAnyChild = false;
                    using var md5 = MD5.Create();

                    for (int ty = 0; ty < factor; ty++)
                    {
                        for (int tx = 0; tx < factor; tx++)
                        {
                            var childCoord = new TileCoord(lodX + tx, lodY + ty);
                            if (lod0Tiles.TryGetValue(childCoord, out var childHash))
                            {
                                hasAnyChild = true;
                                if (cdnMissing != null && cdnMissing.Contains(childHash))
                                {
                                    // Didnt exist on the blizz or community CDNs, but we know it had to have existed at some point
                                    ContentHash.CdnMissingSentinel.CopyTo(hashBytes);
                                }
                                else
                                {
                                    childHash.CopyTo(hashBytes);
                                }
                            }
                            else
                            {
                                // No tile at this position, genuinely no tile at this location
                                hashBytes.Clear();
                            }
                            md5.TransformBlock(hashBytes.ToArray(), 0, 16, null, 0);
                        }
                    }

                    if (!hasAnyChild)
                        continue;

                    md5.TransformFinalBlock([], 0, 0);
                    result[(level, new(lodX, lodY))] = new(md5.Hash!);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Compute LOD hashes and get CompositionLOD dictionaries per level.
    /// </summary>
    public static Dictionary<int, CompositionLOD> ComputeLodLevels(
        IReadOnlyDictionary<TileCoord, ContentHash> lod0Tiles,
        IReadOnlySet<ContentHash>? cdnMissing = null, int maxLod = MinimapComposition.MAX_LOD)
    {
        var flatHashes = ComputeLodHashes(lod0Tiles, cdnMissing, maxLod);
        var result = new Dictionary<int, CompositionLOD>();

        foreach (var ((level, coord), hash) in flatHashes)
        {
            if (!result.TryGetValue(level, out var lod))
            {
                lod = new([]);
                result[level] = lod;
            }
            lod.Tiles[coord] = hash;
        }

        return result;
    }
}
