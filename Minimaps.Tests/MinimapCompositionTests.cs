using Minimaps.Shared.Types;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
            [new (0, 0)] = GetTestHash("hash1"),
            [new (1, 0)] = GetTestHash("hash2"),
            [new (0, 1)] = GetTestHash("hash3")
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
            [new (0, 0)] = GetTestHash("hash1"),
            [new (1, 0)] = GetTestHash("hash2")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new (0, 0)] = GetTestHash("hash1"),
            [new (1, 0)] = GetTestHash("hash3")
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
            [new (10, 5)] = GetTestHash("hashA"),
            [new (0, 0)] = GetTestHash("hashB"),
            [new (5, 10)] = GetTestHash("hashC"),
            [new (-5, 2)] = GetTestHash("hashD")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new (-5, 2)] = GetTestHash("hashD"),
            [new (5, 10)] = GetTestHash("hashC"),
            [new (0, 0)] = GetTestHash("hashB"),
            [new (10, 5)] = GetTestHash("hashA")
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
            [new (1, 0)] = GetTestHash("hash1"),
            [new (0, 1)] = GetTestHash("hash2"),
            [new (0, 0)] = GetTestHash("hash3")
        };

        var tiles2 = new Dictionary<TileCoord, ContentHash>
        {
            [new (0, 0)] = GetTestHash("hash3"),
            [new (0, 1)] = GetTestHash("hash2"),
            [new (1, 0)] = GetTestHash("hash1")
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
            [new (-10, -5)] = GetTestHash("hash1"),
            [new (-1, 0)] = GetTestHash("hash2"),
            [new (0, -1)] = GetTestHash("hash3"),
            [new (5, 10)] = GetTestHash("hash4")
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
            [new (0, 0)] = GetTestHash("singlehash")
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
            [new (0, 0)] = GetTestHash("hash1")
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
            [new (0, 0)] = GetTestHash("hash1"),
            [new (2, 2)] = GetTestHash("hash2")
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
            [new (0, 0)] = GetTestHash("hash1")
        };
        
        var compositionNoMissing = new MinimapComposition(tiles, new HashSet<TileCoord>());
        var compositionWithMissing = new MinimapComposition(tiles, new HashSet<TileCoord> { new (1, 1) });

        Assert.NotEqual(compositionNoMissing.Hash, compositionWithMissing.Hash);
    }

    [Fact]
    public void MissingTiles_SameHash()
    {
        var tiles = new Dictionary<TileCoord, ContentHash>
        {
            [new (0, 0)] = GetTestHash("hash1")
        };
        var missingTiles1 = new HashSet<TileCoord> { new(1, 1), new(2, 2) };
        var missingTiles2 = new HashSet<TileCoord> { new(2, 2), new(1, 1) }; // Different order

        var composition1 = new MinimapComposition(tiles, missingTiles1);
        var composition2 = new MinimapComposition(tiles, missingTiles2);

        Assert.Equal(composition1.Hash, composition2.Hash);
    }

    [Fact]
    public void Json_WithoutMissingTiles()
    {
        var json = $$$"""{"0,0": "{{{GetTestHash("hash1").ToHex()}}}", "1,0": "{{{GetTestHash("hash2").ToHex()}}}", "0,1": "{{{GetTestHash("hash3").ToHex()}}}"}""";
        
        var composition = JsonSerializer.Deserialize<MinimapComposition>(json);
        
        Assert.NotNull(composition);
        Assert.Equal(3, composition.Composition.Count);
        Assert.Empty(composition.MissingTiles);
        Assert.Equal(GetTestHash("hash1"), composition.Composition[new (0, 0)]);
        Assert.Equal(GetTestHash("hash2"), composition.Composition[new (1, 0)]);
        Assert.Equal(GetTestHash("hash3"), composition.Composition[new (0, 1)]);
    }

    [Fact]
    public void Json_WithMissingTiles()
    {
        var json = $$$"""{"_m": ["0,1", "1,0"], "0,0": "{{{GetTestHash("hash1").ToHex()}}}", "2,2": "{{{GetTestHash("hash2").ToHex()}}}"}""";
        
        var composition = JsonSerializer.Deserialize<MinimapComposition>(json);
        
        Assert.NotNull(composition);
        Assert.Equal(2, composition.Composition.Count);
        Assert.Equal(2, composition.MissingTiles.Count);
        
        Assert.Equal(GetTestHash("hash1"), composition.Composition[new (0, 0)]);
        Assert.Equal(GetTestHash("hash2"), composition.Composition[new (2, 2)]);
        
        Assert.Contains(new (0, 1), composition.MissingTiles);
        Assert.Contains(new (1, 0), composition.MissingTiles);
    }

    [Fact]
    public void Json_EmptyMissingArray()
    {
        var json = $$$"""{"_m": [], "0,0": "{{{GetTestHash("hash1").ToHex()}}}"}""";
        
        var composition = JsonSerializer.Deserialize<MinimapComposition>(json);
        
        Assert.NotNull(composition);
        Assert.Single(composition.Composition);
        Assert.Empty(composition.MissingTiles);
        Assert.Equal(GetTestHash("hash1"), composition.Composition[new (0, 0)]);
    }

    [Fact]
    public void Json_OnlyMissingTiles()
    {
        var json = """{"_m": ["0,0", "1,1", "-5,10"]}""";
        
        var composition = JsonSerializer.Deserialize<MinimapComposition>(json);
        
        Assert.NotNull(composition);
        Assert.Empty(composition.Composition);
        Assert.Equal(3, composition.MissingTiles.Count);
        
        Assert.Contains(new (0, 0), composition.MissingTiles);
        Assert.Contains(new (1, 1), composition.MissingTiles);
        Assert.Contains(new (-5, 10), composition.MissingTiles);
    }

    [Fact]
    public void Json_RoundTrip_ExpectedFail()
    {
        var originalComposition = new MinimapComposition(
            new Dictionary<TileCoord, ContentHash> { [new(0, 0)] = GetTestHash("hash1") },
            new HashSet<TileCoord> { new(1, 1) }
        );

        var json = JsonSerializer.Serialize(originalComposition);
        var deserializedComposition = JsonSerializer.Deserialize<MinimapComposition>(json);

        Assert.NotNull(deserializedComposition);
        Assert.Equal(originalComposition.MissingTiles.Count, deserializedComposition.MissingTiles.Count);
    }

    [Fact]
    public void Json_InvalidCoord_Throws()
    {
        var json = $$$"""{"_m": ["invalid"], "0,0": "{{{GetTestHash("hash1").ToHex()}}}"}""";
        
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinimapComposition>(json));
    }

    [Fact]
    public void Json_ThreeValues_Throws()
    {
        var json = $$$"""{"_m": ["1,2,3"], "0,0": "{{{GetTestHash("hash1").ToHex()}}}"}""";
        
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinimapComposition>(json));
    }

    [Fact]
    public void Json_NonNumeric_Throws()
    {
        var json = $$$"""{"_m": ["a,b"], "0,0": "{{{GetTestHash("hash1").ToHex()}}}"}""";
        
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinimapComposition>(json));
    }

    [Fact]
    public void Json_NullValue_Throws()
    {
        var json = $$$"""{"_m": [null], "0,0": "{{{GetTestHash("hash1").ToHex()}}}"}""";
        
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinimapComposition>(json));
    }

    [Fact]
    public void Json_EmptyString_Throws()
    {
        var json = $$$"""{"_m": [""], "0,0": "{{{GetTestHash("hash1").ToHex()}}}"}""";
        
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MinimapComposition>(json));
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
    public void Hash_UniqueForDifferentCoordinatesWithSameTileHash()
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
}