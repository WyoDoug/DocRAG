// DryRunPageEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     A single page that was visited during a dry run.
/// </summary>
public record DryRunPageEntry
{
    public required string Url { get; init; }
    public required int OutOfScopeDepth { get; init; }
    public required bool InScope { get; init; }
    public required int ContentBytes { get; init; }
    public required int LinksFound { get; init; }
}
