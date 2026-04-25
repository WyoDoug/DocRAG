// // RescrubDiff.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     A per-chunk diff produced by rescrub. In dry-run mode the diffs
///     are returned to the caller without writes; in normal mode the
///     first ten diffs are returned as a sample so the caller can spot-
///     check the change before deciding whether to invoke again.
/// </summary>
public record RescrubDiff
{
    /// <summary>
    ///     Chunk id the diff refers to.
    /// </summary>
    public required string ChunkId { get; init; }

    /// <summary>
    ///     Previous QualifiedName before rescrub.
    /// </summary>
    public string? OldQualifiedName { get; init; }

    /// <summary>
    ///     New QualifiedName after rescrub.
    /// </summary>
    public string? NewQualifiedName { get; init; }

    /// <summary>
    ///     Number of Symbols on the chunk before rescrub.
    /// </summary>
    public int OldSymbolCount { get; init; }

    /// <summary>
    ///     Number of Symbols on the chunk after rescrub.
    /// </summary>
    public int NewSymbolCount { get; init; }

    /// <summary>
    ///     Previous category. Null when unchanged.
    /// </summary>
    public DocCategory? OldCategory { get; init; }

    /// <summary>
    ///     New category. Null when unchanged.
    /// </summary>
    public DocCategory? NewCategory { get; init; }
}
