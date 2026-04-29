// ScrapeJobFactoryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Scanning;

#endregion

namespace SaddleRAG.Tests.Scanning;

public sealed class ScrapeJobFactoryTests
{
    [Fact]
    public void CreateFromUrlAllowedUrlPatternsContainsHost()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Contains("docs.example.com", job.AllowedUrlPatterns);
    }

    [Theory]
    [InlineData(@"/blog/")]
    [InlineData(@"/pricing/")]
    [InlineData(@"/login/")]
    [InlineData(@"/search")]
    [InlineData(@"/account/")]
    [InlineData(@"/cart/")]
    [InlineData(@"mailto:")]
    [InlineData(@"#")]
    public void CreateFromUrlExcludedUrlPatternsContainsDefaultJunkPattern(string pattern)
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Contains(pattern, job.ExcludedUrlPatterns);
    }

    [Fact]
    public void CreateFromUrlInScopeDepthIsDefault()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(DefaultInScopeDepth, job.InScopeDepth);
    }

    [Fact]
    public void CreateFromUrlSameHostDepthIsDefault()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(DefaultSameHostDepth, job.SameHostDepth);
    }

    [Fact]
    public void CreateFromUrlOffSiteDepthIsDefault()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(DefaultOffSiteDepth, job.OffSiteDepth);
    }

    [Fact]
    public void CreateFromUrlMaxPagesIsDefault()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(DefaultMaxPages, job.MaxPages);
    }

    [Fact]
    public void CreateFromUrlFetchDelayMsIsDefault()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(DefaultFetchDelayMs, job.FetchDelayMs);
    }

    [Fact]
    public void CreateFromUrlCustomMaxPagesOverridesDefault()
    {
        const int customMaxPages = 250;

        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion, maxPages: customMaxPages);

        Assert.Equal(customMaxPages, job.MaxPages);
    }

    [Fact]
    public void CreateFromUrlCustomFetchDelayMsOverridesDefault()
    {
        const int customDelayMs = 1500;

        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion, fetchDelayMs: customDelayMs);

        Assert.Equal(customDelayMs, job.FetchDelayMs);
    }

    [Fact]
    public void CreateFromUrlHintNullDefaultsToLibraryId()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion, hint: null);

        Assert.Equal(TestLibraryId, job.LibraryHint);
    }

    [Fact]
    public void CreateFromUrlHintProvidedUsesProvidedValue()
    {
        const string customHint = "Example Library v3";

        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion, customHint);

        Assert.Equal(customHint, job.LibraryHint);
    }

    [Fact]
    public void CreateFromUrlLibraryIdIsSetCorrectly()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(TestLibraryId, job.LibraryId);
    }

    [Fact]
    public void CreateFromUrlVersionIsSetCorrectly()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.Equal(TestVersion, job.Version);
    }

    [Fact]
    public void CreateFromUrlForceCleanDefaultsToFalse()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion);

        Assert.False(job.ForceClean);
    }

    [Fact]
    public void CreateFromUrlForceCleanTrueSetsField()
    {
        var job = ScrapeJobFactory.CreateFromUrl(TestUrl, TestLibraryId, TestVersion, forceClean: true);

        Assert.True(job.ForceClean);
    }

    private const string TestUrl = "https://docs.example.com/api/v2/";
    private const string TestLibraryId = "example-lib";
    private const string TestVersion = "3.1.0";
    private const int DefaultMaxPages = 0;
    private const int DefaultFetchDelayMs = 0;
    private const int DefaultInScopeDepth = 10;
    private const int DefaultSameHostDepth = 2;
    private const int DefaultOffSiteDepth = 1;
}
