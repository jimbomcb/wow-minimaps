using Minimaps.Shared.Types;

namespace Minimaps.Tests;

public class MinimapCompositionTests
{
    [Fact]
    public void EmptyComposition()
    {
        var emptyComposition1 = new MinimapComposition(new Dictionary<TileCoord, string>());
        var emptyComposition2 = new MinimapComposition(new Dictionary<TileCoord, string>());

        Assert.Equal(emptyComposition1.Hash, emptyComposition2.Hash);
        Assert.NotNull(emptyComposition1.Hash);
        Assert.NotEmpty(emptyComposition1.Hash);
    }

    [Fact]
    public void SameComposition()
    {
        var tiles = new Dictionary<TileCoord, string>
        {
            [new TileCoord(0, 0)] = "hash1",
            [new TileCoord(1, 0)] = "hash2",
            [new TileCoord(0, 1)] = "hash3"
        };

        var composition1 = new MinimapComposition(tiles);
        var composition2 = new MinimapComposition(tiles);

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void DifferentCompositions_ShouldHaveDifferentHashes()
    {
        var tiles1 = new Dictionary<TileCoord, string>
        {
            [new TileCoord(0, 0)] = "hash1",
            [new TileCoord(1, 0)] = "hash2"
        };

        var tiles2 = new Dictionary<TileCoord, string>
        {
            [new TileCoord(0, 0)] = "hash1",
            [new TileCoord(1, 0)] = "hash3"
        };

        var composition1 = new MinimapComposition(tiles1);
        var composition2 = new MinimapComposition(tiles2);

        Assert.NotEqual(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void SameTilesInDifferentOrder_ShouldHaveSameHash()
    {
        var tiles1 = new Dictionary<TileCoord, string>
        {
            [new TileCoord(10, 5)] = "hashA",
            [new TileCoord(0, 0)] = "hashB",
            [new TileCoord(5, 10)] = "hashC",
            [new TileCoord(-5, 2)] = "hashD"
        };

        var tiles2 = new Dictionary<TileCoord, string>
        {
            [new TileCoord(-5, 2)] = "hashD",
            [new TileCoord(5, 10)] = "hashC",
            [new TileCoord(0, 0)] = "hashB",
            [new TileCoord(10, 5)] = "hashA"
        };

        var composition1 = new MinimapComposition(tiles1);
        var composition2 = new MinimapComposition(tiles2);

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void OrderedByCoordinates_XThenY()
    {
        var tiles1 = new Dictionary<TileCoord, string>
        {
            [new TileCoord(1, 0)] = "hash1",
            [new TileCoord(0, 1)] = "hash2",
            [new TileCoord(0, 0)] = "hash3"
        };

        var tiles2 = new Dictionary<TileCoord, string>
        {
            [new TileCoord(0, 0)] = "hash3",
            [new TileCoord(0, 1)] = "hash2",
            [new TileCoord(1, 0)] = "hash1"
        };

        var composition1 = new MinimapComposition(tiles1);
        var composition2 = new MinimapComposition(tiles2);

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void NegativeCoordinates()
    {
        var tiles = new Dictionary<TileCoord, string>
        {
            [new TileCoord(-10, -5)] = "hash1",
            [new TileCoord(-1, 0)] = "hash2",
            [new TileCoord(0, -1)] = "hash3",
            [new TileCoord(5, 10)] = "hash4"
        };

        var composition = new MinimapComposition(tiles);

        Assert.NotNull(composition.Hash);
        Assert.NotEmpty(composition.Hash);
    }

    [Fact]
    public void SingleTile()
    {
        var tiles = new Dictionary<TileCoord, string>
        {
            [new TileCoord(0, 0)] = "singlehash"
        };

        var composition = new MinimapComposition(tiles);

        Assert.NotNull(composition.Hash);
        Assert.NotEmpty(composition.Hash);
    }
}