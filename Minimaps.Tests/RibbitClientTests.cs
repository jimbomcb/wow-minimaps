using Minimaps.Shared.RibbitClient;

namespace Minimaps.Tests;

public class RibbitClientTests
{
    [Theory]
    [InlineData(RibbitRegion.US)]
    [InlineData(RibbitRegion.EU)]
    public async Task SummaryAsync_ReturnsValidData(RibbitRegion region)
    {
        var client = new RibbitClient(region);
        var response = await client.SummaryAsync();

        Assert.True(response.SequenceId > 0);
        Assert.NotNull(response.Data);
        Assert.NotEmpty(response.Data);

        Assert.Contains(response.Data, p => p.Name == "wow");
        Assert.Contains(response.Data, p => p.Name == "agent");

        foreach (var product in response.Data)
        {
            Assert.NotEmpty(product.Name);
            Assert.True(product.Seqn >= 0);
            Assert.NotNull(product.Flags); // Flags can be empty, but not null
        }
    }

    [Theory]
    [InlineData(RibbitRegion.US, "wow")]
    [InlineData(RibbitRegion.EU, "wow")]
    [InlineData(RibbitRegion.US, "wow_beta")]
    [InlineData(RibbitRegion.US, "wow_classic")]
    [InlineData(RibbitRegion.US, "wow_classic_ptr")]
    [InlineData(RibbitRegion.US, "wowt")]
    [InlineData(RibbitRegion.US, "wowe1")]
    [InlineData(RibbitRegion.US, "wlby")]
    [InlineData(RibbitRegion.US, "viper")]
    [InlineData(RibbitRegion.US, "s1")]
    [InlineData(RibbitRegion.US, "s2")]
    public async Task VersionsAsync_ReturnsValidData(RibbitRegion region, string product)
    {
        var client = new RibbitClient(region);
        var response = await client.VersionsAsync(product);

        Assert.True(response.SequenceId > 0);
        Assert.NotNull(response.Data);
        Assert.NotEmpty(response.Data);
        Assert.True(response.Data.Count > 0, "Expected at least one version for the product.");

        foreach (var version in response.Data)
        {
            Assert.NotEmpty(version.Region);
            Assert.NotEmpty(version.BuildConfig);
            Assert.NotEmpty(version.CDNConfig);
            Assert.NotNull(version.KeyRing); // Can be empty, but not null
            Assert.True(version.BuildId > 0);
            Assert.NotEmpty(version.VersionsName);

            Assert.NotNull(version.ProductConfig); // ProductConfig can potentially be empty, so just check not null
        }
    }

    [Theory]
    [InlineData(RibbitRegion.US, "nonexistent_product")]
    [InlineData(RibbitRegion.EU, "invalid_product_name")]
    public async Task VersionsAsync_ThrowsProductNotFoundException_WhenProductNotFound(RibbitRegion region, string product)
    {
        var client = new RibbitClient(region);

        var exception = await Assert.ThrowsAsync<ProductNotFoundException>(() => client.VersionsAsync(product));

        Assert.Equal(product, exception.Product);
        Assert.Contains(product, exception.Message);
    }


    [Theory]
    [InlineData(RibbitRegion.US, "wow")]
    [InlineData(RibbitRegion.EU, "wow")]
    [InlineData(RibbitRegion.US, "wow_beta")]
    [InlineData(RibbitRegion.US, "wow_classic")]
    [InlineData(RibbitRegion.US, "wow_classic_ptr")]
    [InlineData(RibbitRegion.US, "wowt")]
    [InlineData(RibbitRegion.US, "wowe1")]
    public async Task CDNsAsync_LoadsRows(RibbitRegion region, string product)
    {
        var client = new RibbitClient(region);
        var response = await client.CDNsAsync(product);

        Assert.True(response.SequenceId > 0);
        Assert.NotNull(response.Data);
        Assert.NotEmpty(response.Data);
        Assert.True(response.Data.Count > 0, "Expected at least one version for the product.");

        foreach (var version in response.Data)
        {
            Assert.NotEmpty(version.Name);
            Assert.NotEmpty(version.Path);
            Assert.NotEmpty(version.Hosts);
            Assert.NotEmpty(version.Servers);
            Assert.NotEmpty(version.ConfigPath);
        }
    }


}