// HostScopeFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Crawling;

#endregion

namespace DocRAG.Tests.Crawling;

public sealed class HostScopeFilterTests
{
    [Fact]
    public void NewFilterGatesNothing()
    {
        var filter = new HostScopeFilter();

        Assert.False(filter.IsGated(new Uri("https://example.com/anything")));
        Assert.Empty(filter.GatedPrefixes);
    }

    [Fact]
    public void GatePrefixOfBlocksOtherUrlsSharingFirstSegment()
    {
        var filter = new HostScopeFilter();

        filter.GatePrefixOf(new Uri("https://example.com/products/platform/atlas-data-federation"));

        Assert.True(filter.IsGated(new Uri("https://example.com/products/")));
        Assert.True(filter.IsGated(new Uri("https://example.com/products/consulting")));
        Assert.True(filter.IsGated(new Uri("https://example.com/products/cluster-to-cluster-sync")));
    }

    [Fact]
    public void GatePrefixDoesNotBlockSiblingSegments()
    {
        var filter = new HostScopeFilter();

        filter.GatePrefixOf(new Uri("https://example.com/products/platform"));

        Assert.False(filter.IsGated(new Uri("https://example.com/docs/getting-started")));
        Assert.False(filter.IsGated(new Uri("https://example.com/blog/post")));
        Assert.False(filter.IsGated(new Uri("https://example.com/")));
    }

    [Fact]
    public void GatePrefixIsIdempotent()
    {
        var filter = new HostScopeFilter();

        filter.GatePrefixOf(new Uri("https://example.com/products/a"));
        filter.GatePrefixOf(new Uri("https://example.com/products/b"));
        filter.GatePrefixOf(new Uri("https://example.com/products/c"));

        Assert.Single(filter.GatedPrefixes);
    }

    [Fact]
    public void DistinctFirstSegmentsAccumulate()
    {
        var filter = new HostScopeFilter();

        filter.GatePrefixOf(new Uri("https://example.com/products/x"));
        filter.GatePrefixOf(new Uri("https://example.com/solutions/y"));
        filter.GatePrefixOf(new Uri("https://example.com/it-it/products"));

        Assert.Equal(3, filter.GatedPrefixes.Count);
        Assert.True(filter.IsGated(new Uri("https://example.com/products/anything")));
        Assert.True(filter.IsGated(new Uri("https://example.com/solutions/anything")));
        Assert.True(filter.IsGated(new Uri("https://example.com/it-it/anything")));
    }

    [Theory]
    [InlineData("https://example.com/products/platform/atlas", "/products/")]
    [InlineData("https://example.com/products", "/products/")]
    [InlineData("https://example.com/products/", "/products/")]
    [InlineData("https://example.com/it-it/products", "/it-it/")]
    [InlineData("https://example.com/", "/")]
    [InlineData("https://example.com", "/")]
    public void ExtractFirstSegmentReturnsExpectedPrefix(string url, string expected)
    {
        Assert.Equal(expected, HostScopeFilter.ExtractFirstSegment(new Uri(url)));
    }

    [Fact]
    public void GatedPrefixComparisonIsCaseInsensitive()
    {
        var filter = new HostScopeFilter();

        filter.GatePrefixOf(new Uri("https://example.com/Products/x"));

        Assert.True(filter.IsGated(new Uri("https://example.com/products/y")));
        Assert.True(filter.IsGated(new Uri("https://example.com/PRODUCTS/z")));
    }

}
