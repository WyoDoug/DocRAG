// IngestStatusResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     Response payload from start_ingest. Tells the calling LLM the current
///     ingest state plus the next tool to invoke and the parameters to pass.
/// </summary>
public record IngestStatusResponse
{
    /// <summary>
    ///     The state machine status as a numeric enum value.
    /// </summary>
    public required IngestStatus Status { get; init; }

    /// <summary>
    ///     Human-readable name of <see cref="Status"/> (e.g. "InProgress",
    ///     "ReadyToScrape"). Always populated alongside the numeric Status
    ///     so MCP-facing consumers don't have to memorize the enum values.
    /// </summary>
    public string StatusName => Status.ToString();

    /// <summary>
    ///     Resolved library id (may be inferred from URL when not supplied).
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Resolved version (may default to a placeholder when not supplied).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Source URL the call applies to.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     Name of the next MCP tool the caller should invoke. Empty when
    ///     Status is Ready.
    /// </summary>
    public string NextTool { get; init; } = string.Empty;

    /// <summary>
    ///     Human-readable explanation of what to do next, intended for the
    ///     calling LLM to read and follow.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    ///     Suggested parameters for the next tool, keyed by parameter name.
    ///     Empty when no parameters are needed.
    /// </summary>
    public IReadOnlyDictionary<string, string> NextToolArgs { get; init; }
        = new Dictionary<string, string>();
}
