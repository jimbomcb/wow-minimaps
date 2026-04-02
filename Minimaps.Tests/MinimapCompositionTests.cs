using Minimaps.Shared.Types;
using System.Security.Cryptography;
using System.Text;

namespace Minimaps.Tests;

public class MinimapCompositionTests
{
    private static ContentHash GetTestHash(string input) => new(MD5.HashData(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public void EmptyComposition()
    {
        var emptyComposition1 = new MinimapComposition(new Dictionary<TileCoord, ContentHash>(), new HashSet<TileCoord>());
        var emptyComposition2 = new MinimapComposition(new Dictionary<TileCoord, ContentHash>(), new HashSet<TileCoord>());

        Assert.Equal(emptyComposition1.Hash, emptyComposition2.Hash);
        Assert.NotNull(emptyComposition1.Hash);
        Assert.NotEmpty(emptyComposition1.Hash);
    }

    [Fact]
    public void SameComposition()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1"),
            [new(1, 0)] = GetTestHash("hash2"),
            [new(0, 1)] = GetTestHash("hash3")
        };

        var composition1 = new MinimapComposition(tiles, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(tiles, new HashSet<TileCoord>());

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void DifferentCompositions_DifferentHashes()
    {
        var tiles1 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1"),
            [new(1, 0)] = GetTestHash("hash2")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1"),
            [new(1, 0)] = GetTestHash("hash3")
        };

        var composition1 = new MinimapComposition(tiles1, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(tiles2, new HashSet<TileCoord>());

        Assert.NotEqual(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void DifferentOrder_SameHash()
    {
        var tiles1 = new Dictionary<TileCoord, ContentHash>
        {
            [new(10, 5)] = GetTestHash("hashA"),
            [new(0, 0)] = GetTestHash("hashB"),
            [new(5, 10)] = GetTestHash("hashC"),
            [new(-5, 2)] = GetTestHash("hashD")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new(-5, 2)] = GetTestHash("hashD"),
            [new(5, 10)] = GetTestHash("hashC"),
            [new(0, 0)] = GetTestHash("hashB"),
            [new(10, 5)] = GetTestHash("hashA")
        };

        var composition1 = new MinimapComposition(tiles1, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(tiles2, new HashSet<TileCoord>());

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void OrderedByCoordinates()
    {
        var tiles1 = new Dictionary<TileCoord, ContentHash>
        {
            [new(1, 0)] = GetTestHash("hash1"),
            [new(0, 1)] = GetTestHash("hash2"),
            [new(0, 0)] = GetTestHash("hash3")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash3"),
            [new(0, 1)] = GetTestHash("hash2"),
            [new(1, 0)] = GetTestHash("hash1")
        };

        var composition1 = new MinimapComposition(tiles1, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(tiles2, new HashSet<TileCoord>());

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void NegativeCoordinates()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(-10, -5)] = GetTestHash("hash1"),
            [new(-1, 0)] = GetTestHash("hash2"),
            [new(0, -1)] = GetTestHash("hash3"),
            [new(5, 10)] = GetTestHash("hash4")
        };

        var composition = new MinimapComposition(tiles, new HashSet<TileCoord>());

        Assert.NotNull(composition.Hash);
        Assert.NotEmpty(composition.Hash);
    }

    [Fact]
    public void SingleTile()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("singlehash")
        };

        var composition = new MinimapComposition(tiles, new HashSet<TileCoord>());

        Assert.NotNull(composition.Hash);
        Assert.NotEmpty(composition.Hash);
    }

    [Fact]
    public void MissingTiles_Empty()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1")
        };
        var missingTiles = new HashSet<TileCoord>();

        var composition = new MinimapComposition(tiles, missingTiles);

        Assert.Empty(composition.MissingTiles);
        Assert.NotNull(composition.Hash);
    }

    [Fact]
    public void MissingTiles_WithTiles()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1"),
            [new(2, 2)] = GetTestHash("hash2")
        };
        var missingTiles = new HashSet<TileCoord>
        {
            new(0, 1),
            new(1, 0),
            new(1, 1)
        };

        var composition = new MinimapComposition(tiles, missingTiles);

        Assert.Equal(3, composition.MissingTiles.Count);
        Assert.Contains(new(0, 1), composition.MissingTiles);
        Assert.Contains(new(1, 0), composition.MissingTiles);
        Assert.Contains(new(1, 1), composition.MissingTiles);
    }

    [Fact]
    public void MissingTiles_AffectHash()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1")
        };

        var compositionNoMissing = new MinimapComposition(tiles, new HashSet<TileCoord>());
        var compositionWithMissing = new MinimapComposition(tiles, new HashSet<TileCoord> { new(1, 1) });

        Assert.NotEqual(compositionNoMissing.Hash, compositionWithMissing.Hash);
    }

    [Fact]
    public void MissingTiles_SameHash()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("hash1")
        };
        var missingTiles1 = new HashSet<TileCoord> { new(1, 1), new(2, 2) };
        var missingTiles2 = new HashSet<TileCoord> { new(2, 2), new(1, 1) }; // Different order

        var composition1 = new MinimapComposition(tiles, missingTiles1);
        var composition2 = new MinimapComposition(tiles, missingTiles2);

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void Hash_CoordinateOrdering()
    {
        // Test that X takes precedence over Y in sorting
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(2, 0)] = GetTestHash("tile1"),
            [new(1, 999)] = GetTestHash("tile2"),
            [new(1, 0)] = GetTestHash("tile3")
        };
        var composition1 = new MinimapComposition(tiles, new HashSet<TileCoord>());

        // Same tiles in different creation order
        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new(1, 0)] = GetTestHash("tile3"),
            [new(2, 0)] = GetTestHash("tile1"),
            [new(1, 999)] = GetTestHash("tile2")
        };
        var composition2 = new MinimapComposition(tiles2, new HashSet<TileCoord>());

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void Hash_MissingTilesOrdering()
    {
        var tiles = new Dictionary<TileCoord, ContentHash> { [new(0, 0)] = GetTestHash("hash1") };

        var missing1 = new HashSet<TileCoord> { new(2, 0), new(1, 999), new(1, 0) };
        var missing2 = new HashSet<TileCoord> { new(1, 0), new(1, 999), new(2, 0) };

        var composition1 = new MinimapComposition(tiles, missing1);
        var composition2 = new MinimapComposition(tiles, missing2);

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void Hash_UniqueDiffCoordSameTileHash()
    {
        var tiles1 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("samehash"),
            [new(1, 0)] = GetTestHash("samehash")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 1)] = GetTestHash("samehash"),
            [new(1, 1)] = GetTestHash("samehash")
        };

        var composition1 = new MinimapComposition(tiles1, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(tiles2, new HashSet<TileCoord>());

        Assert.NotEqual(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void Hash_ExpectedHash()
    {
        var contentJson = "{\"tiles\":{\"016662f1b521fc5043fce76f3f48ab7f\":[\"35,31\"],\"01bff988d9cbab711e5f969e2de230e5\":[\"31,30\"],\"097bfac6b3a7b6a394691b4fa4d6aa2e\":[\"31,34\"],\"162005c47d661b69ce31a900d8b72dc1\":[\"35,32\"],\"18f70b35250e34c8ef8bb4cba4432c84\":[\"34,32\"],\"1bd2cc8cbff4efbaa72014122eb828fa\":[\"31,35\"],\"1c9993f42ee8e2edb1a7b6430aa9eac1\":[\"34,34\"],\"1d5ea497258ba37b161bcc5cd8c01dc0\":[\"34,30\"],\"217c095a48abfce1c519f9bf2f550ebb\":[\"31,31\"],\"2881a35e75b5741db3958ea18be9efe4\":[\"33,32\"],\"2a33bf8fdac1a7854d6d08c26e0b87c6\":[\"33,33\"],\"2b7840b0e3e350a5193113396f3bd3bb\":[\"33,30\"],\"34aa813169bef9df2a4091b46920bd22\":[\"32,33\"],\"39d0a39b472213cc57b6726295094cf0\":[\"32,34\"],\"41cf40d8c328f61b5496d2cd7d390665\":[\"30,34\"],\"4e88b838d6b072d84c5c9e03cb287e00\":[\"33,34\"],\"64696101e73f6490dea42147210c8fef\":[\"34,31\"],\"83b7d7e58d4c6d06e7d42caadbd2f5e6\":[\"30,35\"],\"882f74e92be466ec977cfad10748a4c5\":[\"33,31\"],\"9bb6e60838620ddf8eefeb987ab01e37\":[\"32,32\"],\"a1e7b9f0c5ea93af9c2f70fdbe1984e4\":[\"31,32\"],\"a30fd13f62d0e678ea6b6468b3e12298\":[\"34,33\"],\"a724576e2ec50a3dcbf62b72bb3a43cd\":[\"35,30\"],\"af8df198ae64f1099fd38fcdeb89989d\":[\"35,34\"],\"ccc48b4e8591c9df3d5496a5b151c1c5\":[\"31,33\"],\"d6e6a6c69e1db5cd752d2c97a9dc2e4f\":[\"35,33\"],\"e4cf8c82e69d5cee54d62b75fe5e4e92\":[\"32,30\"],\"e6177d2bcae664a17f90a86aa46bc45f\":[\"32,31\"],\"ef3ae8b80605064fadc0515b10c82ef2\":[\"30,30\",\"30,31\",\"30,32\",\"30,33\",\"32,35\",\"33,35\",\"34,35\",\"35,35\"]}}";
        var hashHex = "82E7EC580499437E8FE75DDB069753B7";

        var composition = System.Text.Json.JsonSerializer.Deserialize<MinimapComposition>(contentJson);
        Assert.NotNull(composition);
        Assert.Equal(hashHex, Convert.ToHexString(composition.Hash), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_ExpectedHash2()
    {
        // Same LOD0 data as Hash_ExpectedHash but with whitespace - should produce same hash
        var contentJson = "{\"tiles\": {\"016662f1b521fc5043fce76f3f48ab7f\": [\"35,31\"], \"01bff988d9cbab711e5f969e2de230e5\": [\"31,30\"], \"097bfac6b3a7b6a394691b4fa4d6aa2e\": [\"31,34\"], \"162005c47d661b69ce31a900d8b72dc1\": [\"35,32\"], \"18f70b35250e34c8ef8bb4cba4432c84\": [\"34,32\"], \"1bd2cc8cbff4efbaa72014122eb828fa\": [\"31,35\"], \"1c9993f42ee8e2edb1a7b6430aa9eac1\": [\"34,34\"], \"1d5ea497258ba37b161bcc5cd8c01dc0\": [\"34,30\"], \"217c095a48abfce1c519f9bf2f550ebb\": [\"31,31\"], \"2881a35e75b5741db3958ea18be9efe4\": [\"33,32\"], \"2a33bf8fdac1a7854d6d08c26e0b87c6\": [\"33,33\"], \"2b7840b0e3e350a5193113396f3bd3bb\": [\"33,30\"], \"34aa813169bef9df2a4091b46920bd22\": [\"32,33\"], \"39d0a39b472213cc57b6726295094cf0\": [\"32,34\"], \"41cf40d8c328f61b5496d2cd7d390665\": [\"30,34\"], \"4e88b838d6b072d84c5c9e03cb287e00\": [\"33,34\"], \"64696101e73f6490dea42147210c8fef\": [\"34,31\"], \"83b7d7e58d4c6d06e7d42caadbd2f5e6\": [\"30,35\"], \"882f74e92be466ec977cfad10748a4c5\": [\"33,31\"], \"9bb6e60838620ddf8eefeb987ab01e37\": [\"32,32\"], \"a1e7b9f0c5ea93af9c2f70fdbe1984e4\": [\"31,32\"], \"a30fd13f62d0e678ea6b6468b3e12298\": [\"34,33\"], \"a724576e2ec50a3dcbf62b72bb3a43cd\": [\"35,30\"], \"af8df198ae64f1099fd38fcdeb89989d\": [\"35,34\"], \"ccc48b4e8591c9df3d5496a5b151c1c5\": [\"31,33\"], \"d6e6a6c69e1db5cd752d2c97a9dc2e4f\": [\"35,33\"], \"e4cf8c82e69d5cee54d62b75fe5e4e92\": [\"32,30\"], \"e6177d2bcae664a17f90a86aa46bc45f\": [\"32,31\"], \"ef3ae8b80605064fadc0515b10c82ef2\": [\"30,30\", \"30,31\", \"30,32\", \"30,33\", \"32,35\", \"33,35\", \"34,35\", \"35,35\"]}}";
        var hashHex = "82E7EC580499437E8FE75DDB069753B7";

        var composition = System.Text.Json.JsonSerializer.Deserialize<MinimapComposition>(contentJson);
        Assert.NotNull(composition);
        Assert.Equal(hashHex, Convert.ToHexString(composition.Hash), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_RoundTrip()
    {
        // Only LOD0 is serialized - LOD1+ are derived at runtime and not persisted
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new(0, 0)] = GetTestHash("lod0_tile1"),
            [new(1, 0)] = GetTestHash("lod0_tile2")
        };
        var missingTiles = new HashSet<TileCoord> { new(5, 5), new(-1, -2) };

        var composition = new MinimapComposition(tiles, missingTiles);
        composition.TileSize = 512;

        var json = System.Text.Json.JsonSerializer.Serialize(composition);
        var deserializedComposition = System.Text.Json.JsonSerializer.Deserialize<MinimapComposition>(json);

        Assert.NotNull(deserializedComposition);
        Assert.Equal(composition, deserializedComposition);
        Assert.Equal(composition.Hash, deserializedComposition.Hash);

        Assert.Equal(composition.MissingTiles.Count, deserializedComposition.MissingTiles.Count);
        Assert.True(new HashSet<TileCoord>(composition.MissingTiles).SetEquals(deserializedComposition.MissingTiles));

        var lod0Original = composition.GetLOD(0);
        var lod0Deserialized = deserializedComposition.GetLOD(0);
        Assert.NotNull(lod0Original);
        Assert.NotNull(lod0Deserialized);
        Assert.Equal(lod0Original.Tiles.Count, lod0Deserialized.Tiles.Count);
        foreach (var tile in lod0Original.Tiles)
        {
            Assert.True(lod0Deserialized.Tiles.TryGetValue(tile.Key, out var deserializedHash));
            Assert.Equal(tile.Value, deserializedHash);
        }
    }

    [Fact]
    public void LOD_MultipleLevels()
    {
        var lods = new Dictionary<int, CompositionLOD>
        {
            [0] = new(new() { [new(0, 0)] = GetTestHash("lod0_tile1"), [new(1, 0)] = GetTestHash("lod0_tile2") }),
            [1] = new(new() { [new(0, 0)] = GetTestHash("lod1_tile1") }),
            [2] = new(new() { [new(0, 0)] = GetTestHash("lod2_tile1") })
        };

        var composition = new MinimapComposition(lods, new HashSet<TileCoord>());

        Assert.NotNull(composition.GetLOD(0));
        Assert.NotNull(composition.GetLOD(1));
        Assert.NotNull(composition.GetLOD(2));
        Assert.Null(composition.GetLOD(3));

        Assert.Equal(2, composition.GetLOD(0)!.Tiles.Count);
        Assert.Single(composition.GetLOD(1)!.Tiles);
        Assert.Single(composition.GetLOD(2)!.Tiles);
    }

    // LOD_DifferentLevels_DifferentHashes removed: hash is now LOD0-only,
    // so compositions with same LOD0 but different LOD1+ correctly hash identically.

    [Fact]
    public void LOD_SameLevels_SameHash()
    {
        var lods1 = new Dictionary<int, CompositionLOD>
        {
            [0] = new(new() { [new(0, 0)] = GetTestHash("tile1") }),
            [1] = new(new() { [new(0, 0)] = GetTestHash("lod1_tile") })
        };

        var lods2 = new Dictionary<int, CompositionLOD>
        {
            [1] = new(new() { [new(0, 0)] = GetTestHash("lod1_tile") }),
            [0] = new(new() { [new(0, 0)] = GetTestHash("tile1") })
        };

        var composition1 = new MinimapComposition(lods1, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(lods2, new HashSet<TileCoord>());

        Assert.Equal(composition1.Hash, composition2.Hash);
    }
}