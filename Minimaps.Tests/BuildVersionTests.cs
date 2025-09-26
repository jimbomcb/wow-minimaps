using Minimaps.Shared;

namespace Minimaps.Tests;

public class BuildVersionTests
{
    [Theory]
    [InlineData("4095.1023.1023.12345678")]
    [InlineData("11.0.7.58046")]
    [InlineData("10.2.7.54577")]
    [InlineData("9.1.5.40196")]
    [InlineData("1.15.4.54590")]
    public void ParseToStringRoundTrip(string versionString)
    {
        var buildVersion = BuildVersion.Parse(versionString);
        var result = buildVersion.ToString();

        Assert.Equal(versionString, result);
    }

    [Fact]
    public void Components()
    {
        var buildVersion = BuildVersion.Parse("11.0.7.58046");

        Assert.Equal(11, buildVersion.Expansion);
        Assert.Equal(0, buildVersion.Major);
        Assert.Equal(7, buildVersion.Minor);
        Assert.Equal(58046, buildVersion.Build);
    }

    [Fact]
    public void Sorting()
    {
        var versions = new[]
        {
            BuildVersion.Parse("11.0.7.58046"),
            BuildVersion.Parse("11.0.5.57171"),
            BuildVersion.Parse("10.2.7.54577"),
            BuildVersion.Parse("11.1.0.58238")
        };

        Array.Sort(versions);

        // Should be sorted: 10.2.7 < 11.0.5 < 11.0.7 < 11.1.0
        Assert.Equal("10.2.7.54577", versions[0].ToString());
        Assert.Equal("11.0.5.57171", versions[1].ToString());
        Assert.Equal("11.0.7.58046", versions[2].ToString());
        Assert.Equal("11.1.0.58238", versions[3].ToString());
    }

    [Fact]
    public void Comparison()
    {
        var newer = BuildVersion.Parse("11.0.7.58046");
        var older = BuildVersion.Parse("11.0.5.57171");

        Assert.True(newer > older);
        Assert.True(older < newer);
        Assert.False(newer == older);
        Assert.True(newer != older);
    }

    [Fact]
    public void ImplicitConversion()
    {
        var buildVersion = BuildVersion.Parse("11.0.7.58046");

        long longValue = (long)buildVersion;
        Assert.True(longValue > 0);

        BuildVersion convertedBack = (BuildVersion)longValue;
        Assert.Equal(buildVersion, convertedBack);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11.0.7")]
    [InlineData("11.0.7.58046.1")]
    [InlineData("invalid.version")]
    public void ParseInvalidVersion(string invalidVersion)
    {
        Assert.ThrowsAny<Exception>(() => BuildVersion.Parse(invalidVersion));
    }

    [Theory]
    [InlineData("4096.1024.1024.12345678")]
    [InlineData("-1.1.1.12345678")]
    public void ParseOutOfRangeVersion(string invalidVersion)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => BuildVersion.Parse(invalidVersion));
    }

    [Fact]
    public void TryParse()
    {
        var validResult = BuildVersion.TryParse("11.0.7.58046", out var validVersion);
        Assert.True(validResult);
        Assert.Equal("11.0.7.58046", validVersion.ToString());

        var invalidResult = BuildVersion.TryParse("invalid", out var invalidVersion);
        Assert.False(invalidResult);
        Assert.Equal(default, invalidVersion);
    }
}