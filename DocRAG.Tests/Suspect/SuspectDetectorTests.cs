// SuspectDetectorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

using DocRAG.Ingestion.Suspect;

namespace DocRAG.Tests.Suspect;

public sealed class SuspectDetectorTests
{
    [Fact]
    public async Task OnePagerFlagsBelowThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://example.com",
            pageCount: 2, distinctHostCount: 1, distinctLinkTargets: 50,
            languageMix: new Dictionary<string, double> { ["csharp"] = 1.0 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "About" },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(SuspectReason.OnePager, reasons);
    }

    [Fact]
    public async Task LanguageMismatchFlagsWhenNoDeclaredLanguageAboveThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://example.com",
            pageCount: 100, distinctHostCount: 1, distinctLinkTargets: 50,
            languageMix: new Dictionary<string, double> { ["go"] = 0.5, ["ruby"] = 0.5 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "Some doc" },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(SuspectReason.LanguageMismatch, reasons);
    }

    [Fact]
    public async Task HealthyLibraryNoReasons()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://docs.example.com",
            pageCount: 500, distinctHostCount: 3, distinctLinkTargets: 1000,
            languageMix: new Dictionary<string, double> { ["csharp"] = 0.9 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "Tutorial", "Reference" },
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(reasons);
    }

    [Fact]
    public async Task ReadmeOnlyFlagsGitHubRootWithReadmeTitles()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://github.com/foo/bar",
            pageCount: 1, distinctHostCount: 1, distinctLinkTargets: 50,
            languageMix: new Dictionary<string, double> { ["csharp"] = 1.0 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "README - foo/bar" },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(SuspectReason.ReadmeOnly, reasons);
    }

    [Fact]
    public async Task SparseLinkGraphFlagsBelowThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://docs.example.com",
            pageCount: 100, distinctHostCount: 2, distinctLinkTargets: 5,
            languageMix: new Dictionary<string, double> { ["csharp"] = 0.9 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "Tutorial" },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(SuspectReason.SparseLinkGraph, reasons);
    }
}
