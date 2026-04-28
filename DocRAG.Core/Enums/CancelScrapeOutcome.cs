// CancelScrapeOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Outcome of a cancel_scrape MCP tool call. Signalled means an
///     active runner's CancellationTokenSource was triggered;
///     OrphanCleanedUp means the DB row was updated directly because
///     no active runner existed (process restart). AlreadyTerminal
///     and NotFound are no-op cases.
/// </summary>
public enum CancelScrapeOutcome
{
    Signalled,
    OrphanCleanedUp,
    AlreadyTerminal,
    NotFound
}
