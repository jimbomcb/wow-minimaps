using Minimaps.Shared.Types;
using System.Security.Cryptography;

namespace Minimaps.Tests;

public class LodHashCalculatorTests
{
    private static ContentHash Hash(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return new ContentHash(MD5.HashData(bytes));
    }

    [Fact]
    public void EmptyGrid_ReturnsEmpty()
    {
        var lod0 = new Dictionary<TileCoord, ContentHash>();
        var result = LodHashCalculator.ComputeLodHashes(lod0, null);
        Assert.Empty(result);
    }

    [Fact]
    public void SingleTile_ProducesLodTilesAtAllLevels()
    {
        var lod0 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = Hash("tile_a")
        };

        var result = LodHashCalculator.ComputeLodHashes(lod0, null);

        // Single tile at (0,0) should produce one LOD tile per level (1-6)
        for (int level = 1; level <= 6; level++)
        {
            Assert.True(result.ContainsKey((level, new TileCoord(0, 0))),
                $"Expected LOD{level} tile at (0,0)");
        }
    }

    [Fact]
    public void CdnMissing_ProducesDifferentHash()
    {
        var tileHash = Hash("tile_a");
        var lod0 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = tileHash,
            [new(1, 0)] = Hash("tile_b")
        };

        var withoutMissing = LodHashCalculator.ComputeLodHashes(lod0, null);
        var withMissing = LodHashCalculator.ComputeLodHashes(lod0, new HashSet<ContentHash> { tileHash });

        // LOD1 tile at (0,0) should differ when tile_a is cdn_missing
        var lod1Key = (1, new TileCoord(0, 0));
        Assert.NotEqual(withoutMissing[lod1Key], withMissing[lod1Key]);
    }

    [Fact]
    public void CdnMissing_DiffersFromBuildMissing()
    {
        // Scenario: tile at (1,0) is cdn_missing vs not present at all
        var tileHash = Hash("tile_b");

        var withCdnMissing = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = Hash("tile_a"),
            [new(1, 0)] = tileHash // present in composition, marked cdn_missing
        };

        var withBuildMissing = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = Hash("tile_a")
            // (1,0) not present at all
        };

        var cdnResult = LodHashCalculator.ComputeLodHashes(withCdnMissing, new HashSet<ContentHash> { tileHash });
        var buildResult = LodHashCalculator.ComputeLodHashes(withBuildMissing, null);

        // LOD1 hashes should differ: cdn_missing uses 0x67 sentinel, build-missing uses 0x00
        var lod1Key = (1, new TileCoord(0, 0));
        Assert.NotEqual(cdnResult[lod1Key], buildResult[lod1Key]);
    }

    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        var lod0 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = Hash("a"),
            [new(1, 0)] = Hash("b"),
            [new(0, 1)] = Hash("c"),
            [new(1, 1)] = Hash("d")
        };

        var result1 = LodHashCalculator.ComputeLodHashes(lod0, null);
        var result2 = LodHashCalculator.ComputeLodHashes(lod0, null);

        Assert.Equal(result1.Count, result2.Count);
        foreach (var (key, hash) in result1)
        {
            Assert.Equal(hash, result2[key]);
        }
    }

    [Fact]
    public void ComputeLodLevels_ReturnsOrganizedByLevel()
    {
        var lod0 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = Hash("tile")
        };

        var levels = LodHashCalculator.ComputeLodLevels(lod0);

        Assert.Equal(6, levels.Count); // LOD1-6
        for (int i = 1; i <= 6; i++)
        {
            Assert.True(levels.ContainsKey(i));
            Assert.Single(levels[i].Tiles);
        }
    }
}
