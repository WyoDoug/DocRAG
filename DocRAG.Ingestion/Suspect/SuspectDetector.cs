// SuspectDetector.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Ingestion.Suspect;

/// <summary>
///     Pure post-scrape heuristics for "this URL probably isn't docs."
///     Five reasons surfaced: OnePager, SparseLinkGraph, SingleHost,
///     LanguageMismatch, ReadmeOnly. No I/O — all inputs are passed in
///     by the caller (IngestionOrchestrator).
/// </summary>
public sealed class SuspectDetector
{
    public Task<IReadOnlyList<string>> EvaluateAsync(string libraryId,
                                                     string version,
                                                     string rootUrl,
                                                     int pageCount,
                                                     int distinctHostCount,
                                                     int distinctLinkTargets,
                                                     IReadOnlyDictionary<string, double> languageMix,
                                                     IReadOnlyList<string> declaredLanguages,
                                                     IReadOnlyList<string> sampleTitles,
                                                     CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(rootUrl);
        ArgumentNullException.ThrowIfNull(languageMix);
        ArgumentNullException.ThrowIfNull(declaredLanguages);
        ArgumentNullException.ThrowIfNull(sampleTitles);

        var reasons = new List<string>();

        if (pageCount <= OnePagerThreshold)
            reasons.Add(SuspectReason.OnePager);

        if (distinctLinkTargets < SparseLinkThreshold)
            reasons.Add(SuspectReason.SparseLinkGraph);

        if (distinctHostCount == 1 && declaredLanguages.Count > 1)
            reasons.Add(SuspectReason.SingleHost);

        if (declaredLanguages.Count > 0)
        {
            bool anyDeclaredAboveThreshold = declaredLanguages.Any(d =>
                languageMix.GetValueOrDefault(d.ToLowerInvariant(), 0.0) >= LanguageMatchThreshold);
            if (!anyDeclaredAboveThreshold)
                reasons.Add(SuspectReason.LanguageMismatch);
        }

        bool isGitHubRoot = Uri.TryCreate(rootUrl, UriKind.Absolute, out var u)
                            && u.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase);
        bool readmeOnly = isGitHubRoot && sampleTitles.Count > 0 &&
                          sampleTitles.All(t => t.Contains(ReadmeMarker, StringComparison.OrdinalIgnoreCase));
        if (readmeOnly)
            reasons.Add(SuspectReason.ReadmeOnly);

        var result = (IReadOnlyList<string>) reasons;
        return Task.FromResult(result);
    }

    private const int OnePagerThreshold = 3;
    private const int SparseLinkThreshold = 10;
    private const double LanguageMatchThreshold = 0.30;
    private const string GitHubHost = "github.com";
    private const string ReadmeMarker = "readme";
}
