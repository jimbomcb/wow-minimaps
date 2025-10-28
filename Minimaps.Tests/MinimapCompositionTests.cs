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
        var contentJson = "{\"lod\":{\"0\":{\"016662f1b521fc5043fce76f3f48ab7f\":[\"35,31\"],\"01bff988d9cbab711e5f969e2de230e5\":[\"31,30\"],\"097bfac6b3a7b6a394691b4fa4d6aa2e\":[\"31,34\"],\"162005c47d661b69ce31a900d8b72dc1\":[\"35,32\"],\"18f70b35250e34c8ef8bb4cba4432c84\":[\"34,32\"],\"1bd2cc8cbff4efbaa72014122eb828fa\":[\"31,35\"],\"1c9993f42ee8e2edb1a7b6430aa9eac1\":[\"34,34\"],\"1d5ea497258ba37b161bcc5cd8c01dc0\":[\"34,30\"],\"217c095a48abfce1c519f9bf2f550ebb\":[\"31,31\"],\"2881a35e75b5741db3958ea18be9efe4\":[\"33,32\"],\"2a33bf8fdac1a7854d6d08c26e0b87c6\":[\"33,33\"],\"2b7840b0e3e350a5193113396f3bd3bb\":[\"33,30\"],\"34aa813169bef9df2a4091b46920bd22\":[\"32,33\"],\"39d0a39b472213cc57b6726295094cf0\":[\"32,34\"],\"41cf40d8c328f61b5496d2cd7d390665\":[\"30,34\"],\"4e88b838d6b072d84c5c9e03cb287e00\":[\"33,34\"],\"64696101e73f6490dea42147210c8fef\":[\"34,31\"],\"83b7d7e58d4c6d06e7d42caadbd2f5e6\":[\"30,35\"],\"882f74e92be466ec977cfad10748a4c5\":[\"33,31\"],\"9bb6e60838620ddf8eefeb987ab01e37\":[\"32,32\"],\"a1e7b9f0c5ea93af9c2f70fdbe1984e4\":[\"31,32\"],\"a30fd13f62d0e678ea6b6468b3e12298\":[\"34,33\"],\"a724576e2ec50a3dcbf62b72bb3a43cd\":[\"35,30\"],\"af8df198ae64f1099fd38fcdeb89989d\":[\"35,34\"],\"ccc48b4e8591c9df3d5496a5b151c1c5\":[\"31,33\"],\"d6e6a6c69e1db5cd752d2c97a9dc2e4f\":[\"35,33\"],\"e4cf8c82e69d5cee54d62b75fe5e4e92\":[\"32,30\"],\"e6177d2bcae664a17f90a86aa46bc45f\":[\"32,31\"],\"ef3ae8b80605064fadc0515b10c82ef2\":[\"30,30\",\"30,31\",\"30,32\",\"30,33\",\"32,35\",\"33,35\",\"34,35\",\"35,35\"]},\"1\":{\"296b6faaf2009cd98ddc564a256e18ea\":[\"34,32\"],\"6891869d8019f33296f23f9c6f6094bc\":[\"34,34\"],\"700793d63c12bd8dc80178a18bf474f1\":[\"32,32\"],\"7068e463a271b76cf7244eb77f706019\":[\"30,32\"],\"94a13e8028cabe288c15da7ee8df0fd8\":[\"30,30\"],\"9fd80e9e724eca8b1f13f6621777b07e\":[\"34,30\"],\"a5a21b8891d9d2d4361653a54e42dd81\":[\"32,34\"],\"bddaf2faf2182049afe7ab392aa8de4a\":[\"32,30\"],\"c72c2097340d1ff17f41dca1d7fc5848\":[\"30,34\"]},\"2\":{\"0c983319b899aeec179f31c5209b828d\":[\"32,32\"],\"4c4b23e4916d32ccb263d74d61e26130\":[\"32,28\"],\"5b81e55be0ae3dbb7063cd54ce8d8fa4\":[\"28,28\"],\"b190a53540213eacf2a1524b14b1dde0\":[\"28,32\"]},\"3\":{\"2a699e220b5e0baf87a5f23f18acaa81\":[\"32,24\"],\"b64f2159854bb535992ef88452ea5627\":[\"24,24\"],\"e0d5265f7a9a91efcbe696c1feadf8c7\":[\"24,32\"],\"e7041a490817a0d48a9e9a722bfa019c\":[\"32,32\"]},\"4\":{\"162f4b81d12ae687798e91fcd3bcb458\":[\"32,16\"],\"22f28f93319ea210e8252c1702a1e8d3\":[\"32,32\"],\"868ce1d8b2383070269cc4569212303f\":[\"16,16\"],\"a4b7752236fb9ce8a375b7b5180cd1f8\":[\"16,32\"]},\"5\":{\"18b96cf6995ebc93252096dfac02e883\":[\"0,32\"],\"6adba4cc7eb4837f3557d904d4040944\":[\"0,0\"],\"6f0649e466f4ee8f1367022bcf85cdfd\":[\"32,32\"],\"efa4618292f71523e26ef26f31d6d348\":[\"32,0\"]},\"6\":{\"84dba2c5c7de468547f8e943fcf91799\":[\"0,0\"]}}}";
        var hashHex = "670F9699284AA6A970649BE0D58981B8";

        var composition = System.Text.Json.JsonSerializer.Deserialize<MinimapComposition>(contentJson);
        Assert.NotNull(composition);
        Assert.Equal(hashHex, Convert.ToHexString(composition.Hash), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_ExpectedHash2()
    {
        var contentJson = "{\"lod\": {\"0\": {\"016662f1b521fc5043fce76f3f48ab7f\": [\"35,31\"], \"01bff988d9cbab711e5f969e2de230e5\": [\"31,30\"], \"097bfac6b3a7b6a394691b4fa4d6aa2e\": [\"31,34\"], \"162005c47d661b69ce31a900d8b72dc1\": [\"35,32\"], \"18f70b35250e34c8ef8bb4cba4432c84\": [\"34,32\"], \"1bd2cc8cbff4efbaa72014122eb828fa\": [\"31,35\"], \"1c9993f42ee8e2edb1a7b6430aa9eac1\": [\"34,34\"], \"1d5ea497258ba37b161bcc5cd8c01dc0\": [\"34,30\"], \"217c095a48abfce1c519f9bf2f550ebb\": [\"31,31\"], \"2881a35e75b5741db3958ea18be9efe4\": [\"33,32\"], \"2a33bf8fdac1a7854d6d08c26e0b87c6\": [\"33,33\"], \"2b7840b0e3e350a5193113396f3bd3bb\": [\"33,30\"], \"34aa813169bef9df2a4091b46920bd22\": [\"32,33\"], \"39d0a39b472213cc57b6726295094cf0\": [\"32,34\"], \"41cf40d8c328f61b5496d2cd7d390665\": [\"30,34\"], \"4e88b838d6b072d84c5c9e03cb287e00\": [\"33,34\"], \"64696101e73f6490dea42147210c8fef\": [\"34,31\"], \"83b7d7e58d4c6d06e7d42caadbd2f5e6\": [\"30,35\"], \"882f74e92be466ec977cfad10748a4c5\": [\"33,31\"], \"9bb6e60838620ddf8eefeb987ab01e37\": [\"32,32\"], \"a1e7b9f0c5ea93af9c2f70fdbe1984e4\": [\"31,32\"], \"a30fd13f62d0e678ea6b6468b3e12298\": [\"34,33\"], \"a724576e2ec50a3dcbf62b72bb3a43cd\": [\"35,30\"], \"af8df198ae64f1099fd38fcdeb89989d\": [\"35,34\"], \"ccc48b4e8591c9df3d5496a5b151c1c5\": [\"31,33\"], \"d6e6a6c69e1db5cd752d2c97a9dc2e4f\": [\"35,33\"], \"e4cf8c82e69d5cee54d62b75fe5e4e92\": [\"32,30\"], \"e6177d2bcae664a17f90a86aa46bc45f\": [\"32,31\"], \"ef3ae8b80605064fadc0515b10c82ef2\": [\"30,30\", \"30,31\", \"30,32\", \"30,33\", \"32,35\", \"33,35\", \"34,35\", \"35,35\"]}, \"1\": {\"296b6faaf2009cd98ddc564a256e18ea\": [\"34,32\"], \"6891869d8019f33296f23f9c6f6094bc\": [\"34,34\"], \"700793d63c12bd8dc80178a18bf474f1\": [\"32,32\"], \"7068e463a271b76cf7244eb77f706019\": [\"30,32\"], \"94a13e8028cabe288c15da7ee8df0fd8\": [\"30,30\"], \"9fd80e9e724eca8b1f13f6621777b07e\": [\"34,30\"], \"a5a21b8891d9d2d4361653a54e42dd81\": [\"32,34\"], \"bddaf2faf2182049afe7ab392aa8de4a\": [\"32,30\"], \"c72c2097340d1ff17f41dca1d7fc5848\": [\"30,34\"]}, \"2\": {\"0c983319b899aeec179f31c5209b828d\": [\"32,32\"], \"4c4b23e4916d32ccb263d74d61e26130\": [\"32,28\"], \"5b81e55be0ae3dbb7063cd54ce8d8fa4\": [\"28,28\"], \"b190a53540213eacf2a1524b14b1dde0\": [\"28,32\"]}, \"3\": {\"2a699e220b5e0baf87a5f23f18acaa81\": [\"32,24\"], \"b64f2159854bb535992ef88452ea5627\": [\"24,24\"], \"e0d5265f7a9a91efcbe696c1feadf8c7\": [\"24,32\"], \"e7041a490817a0d48a9e9a722bfa019c\": [\"32,32\"]}, \"4\": {\"162f4b81d12ae687798e91fcd3bcb458\": [\"32,16\"], \"22f28f93319ea210e8252c1702a1e8d3\": [\"32,32\"], \"868ce1d8b2383070269cc4569212303f\": [\"16,16\"], \"a4b7752236fb9ce8a375b7b5180cd1f8\": [\"16,32\"]}, \"5\": {\"18b96cf6995ebc93252096dfac02e883\": [\"0,32\"], \"6adba4cc7eb4837f3557d904d4040944\": [\"0,0\"], \"6f0649e466f4ee8f1367022bcf85cdfd\": [\"32,32\"], \"efa4618292f71523e26ef26f31d6d348\": [\"32,0\"]}, \"6\": {\"84dba2c5c7de468547f8e943fcf91799\": [\"0,0\"]}}}";
        var hashHex = "670F9699284AA6A970649BE0D58981B8";

        var composition = System.Text.Json.JsonSerializer.Deserialize<MinimapComposition>(contentJson);
        Assert.NotNull(composition);
        Assert.Equal(hashHex, Convert.ToHexString(composition.Hash), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_RoundTrip()
    {
        var lods = new Dictionary<int, CompositionLOD>
        {
            [0] = new(new() { [new(0, 0)] = GetTestHash("lod0_tile1"), [new(1, 0)] = GetTestHash("lod0_tile2") }),
            [1] = new(new() { [new(0, 0)] = GetTestHash("lod1_tile1") })
        };
        var missingTiles = new HashSet<TileCoord> { new(5, 5), new(-1, -2) };

        var composition = new MinimapComposition(lods, missingTiles);

        var json = System.Text.Json.JsonSerializer.Serialize(composition);
        var deserializedComposition = System.Text.Json.JsonSerializer.Deserialize<MinimapComposition>(json);

        Assert.NotNull(deserializedComposition);
        Assert.Equal(composition, deserializedComposition);
        Assert.Equal(composition.Hash, deserializedComposition.Hash);

        Assert.Equal(composition.MissingTiles.Count, deserializedComposition.MissingTiles.Count);
        Assert.True(new HashSet<TileCoord>(composition.MissingTiles).SetEquals(deserializedComposition.MissingTiles));

        for (int i = 0; i <= MinimapComposition.MAX_LOD; i++)
        {
            var lodOriginal = composition.GetLOD(i);
            var lodDeserialized = deserializedComposition.GetLOD(i);

            if (lodOriginal == null)
            {
                Assert.Null(lodDeserialized);
                continue;
            }

            Assert.NotNull(lodDeserialized);
            Assert.Equal(lodOriginal.Tiles.Count, lodDeserialized.Tiles.Count);
            foreach (var tile in lodOriginal.Tiles)
            {
                Assert.True(lodDeserialized.Tiles.TryGetValue(tile.Key, out var deserializedHash));
                Assert.Equal(tile.Value, deserializedHash);
            }
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

    [Fact]
    public void LOD_DifferentLevels_DifferentHashes()
    {
        var lods1 = new Dictionary<int, CompositionLOD>
        {
            [0] = new(new() { [new(0, 0)] = GetTestHash("tile1") }),
            [1] = new(new() { [new(0, 0)] = GetTestHash("lod_tile") })
        };

        var lods2 = new Dictionary<int, CompositionLOD>
        {
            [0] = new(new() { [new(0, 0)] = GetTestHash("tile1") }),
            [2] = new(new() { [new(0, 0)] = GetTestHash("lod_tile") })
        };

        var composition1 = new MinimapComposition(lods1, new HashSet<TileCoord>());
        var composition2 = new MinimapComposition(lods2, new HashSet<TileCoord>());

        Assert.NotEqual(composition1.Hash, composition2.Hash);
    }

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