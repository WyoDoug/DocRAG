// ScrapeJobCandidateStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Status of a project-scanned scrape job candidate in the review queue.
/// </summary>
public enum ScrapeJobCandidateStatus
{
    Pending,
    Approved,
    Rejected,
    Ingesting,
    Completed
}
