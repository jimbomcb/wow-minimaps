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