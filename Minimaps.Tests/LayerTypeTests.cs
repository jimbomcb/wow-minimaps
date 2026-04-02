using Minimaps.Shared.Types;

namespace Minimaps.Tests;

/// <summary>
/// Ensure that they're registered with one or the other, don't want to add a type and forget to assign it.
/// </summary>
public class LayerTypeTests
{
    [Fact]
    public void AllLayerTypes_AreClassified_AsExactlyOneCategory()
    {
        foreach (var type in Enum.GetValues<LayerType>())
        {
            bool isComp = type.IsCompositionLayer();
            bool isData = type.IsDataLayer();
            Assert.True(isComp != isData,
                $"LayerType.{type} must be exactly one of composition or data (composition={isComp}, data={isData})");
        }
    }

    [Fact]
    public void Count_MatchesDefinedValues()
    {
        Assert.Equal(LayerTypeExtensions.Count, Enum.GetValues<LayerType>().Length);
    }

    [Theory]
    [InlineData(LayerType.Minimap, true)]
    [InlineData(LayerType.MapTexture, true)]
    [InlineData(LayerType.NoLiquid, false)]
    [InlineData(LayerType.Impass, false)]
    [InlineData(LayerType.AreaId, false)]
    public void IsBaseLayer_CorrectForAllTypes(LayerType type, bool expected)
    {
        Assert.Equal(expected, type.IsBaseLayer());
    }
}
