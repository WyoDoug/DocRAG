// ScrapeJobCandidate.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Auto-generated scrape job candidate from project scanning.
///     Goes through a review queue before ingestion.
/// </summary>
public class ScrapeJobCandidate
{
    /// <summary>
    ///     NuGet package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    ///     Package version discovered in the project.
    /// </summary>
    public required string PackageVersion { get; init; }

    /// <summary>
    ///     ProjectUrl from NuGet metadata.
    /// </summary>
    public required string NuGetProjectUrl { get; init; }

    /// <summary>
    ///     Repository URL from NuGet metadata, if available.
    /// </summary>
    public required string? NuGetRepositoryUrl { get; init; }

    /// <summary>
    ///     Pre-populated ScrapeJob ready for approval/adjustment.
    /// </summary>
    public required ScrapeJob GeneratedScrapeJob { get; init; }

    /// <summary>
    ///     Why the scanner thinks this is worth scraping.
    /// </summary>
    public required string Rationale { get; init; }

    /// <summary>
    ///     Confidence that the auto-generated ScrapeJob will produce
    ///     good results without manual adjustment.
    /// </summary>
    public required ScanConfidence Confidence { get; init; }

    /// <summary>
    ///     Current status in the review queue.
    /// </summary>
    public ScrapeJobCandidateStatus Status { get; set; } = ScrapeJobCandidateStatus.Pending;
}
