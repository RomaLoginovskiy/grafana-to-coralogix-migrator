using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

public sealed class RegionMapperTests
{
    /// <summary>
    /// KnownRegions drives the interactive region pickers, so an entry the mapper cannot resolve would be
    /// selectable and then throw on the very next line.
    /// </summary>
    [Fact]
    public void KnownRegions_EveryEntryResolvesToBothEndpoints()
    {
        Assert.NotEmpty(RegionMapper.KnownRegions);

        foreach (var region in RegionMapper.KnownRegions)
        {
            Assert.StartsWith("https://", RegionMapper.Resolve(region));
            Assert.EndsWith("/mgmt/openapi/latest", RegionMapper.Resolve(region));
            Assert.EndsWith("/grafana", RegionMapper.ResolveGrafana(region));
        }
    }

    /// <summary>
    /// The list is maintained by hand next to the URL table; this catches a region added to one and not
    /// the other, which would otherwise only surface as a missing picker entry.
    /// </summary>
    [Fact]
    public void KnownRegions_CoversEveryMappedRegion()
    {
        string[] expected = ["eu1", "eu2", "us1", "us2", "ap1", "ap2", "ap3", "in1"];

        Assert.Equal(expected, RegionMapper.KnownRegions.ToArray());
    }

    [Theory]
    [InlineData("EU1", "eu1")]
    [InlineData("eu1", "eu1")]
    [InlineData("In1", "in1")]
    public void Normalize_KnownRegion_ReturnsCanonicalLowercaseSpelling(string input, string expected)
    {
        Assert.Equal(expected, RegionMapper.Normalize(input));
    }

    [Theory]
    [InlineData("eu9")]
    [InlineData("")]
    [InlineData(" eu1")]
    [InlineData(null)]
    public void Normalize_UnknownOrBlankRegion_ReturnsNull(string? input)
    {
        Assert.Null(RegionMapper.Normalize(input));
    }

    [Fact]
    public void Resolve_UnknownRegion_ThrowsWithTheValidRegionsListed()
    {
        var ex = Assert.Throws<ArgumentException>(() => RegionMapper.Resolve("eu9"));

        Assert.Contains("eu1", ex.Message);
    }
}
