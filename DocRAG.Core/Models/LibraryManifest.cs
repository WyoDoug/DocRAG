// LibraryManifest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Versioning metadata for a library's indexed state. Lets the rescrub
///     tool's auto-detect logic decide whether classification needs to
///     re-run (profile change → yes; parser-only change → no).
/// </summary>
public record LibraryManifest
{
    /// <summary>
    ///     ParserVersion that was current when this library was last built or rescrubbed.
    /// </summary>
    public int LastParserVersion { get; init; }

    /// <summary>
    ///     SHA-256 of the canonical-JSON-serialized LibraryProfile that was active
    ///     when this library was last built or rescrubbed. Mismatch with the current
    ///     profile hash signals that classification depends on a profile that has changed.
    /// </summary>
    public string LastProfileHash { get; init; } = string.Empty;

    /// <summary>
    ///     ClassifierVersion (model name + prompt-hash prefix) that was current
    ///     when this library was last classified.
    /// </summary>
    public string LastClassifierVersion { get; init; } = string.Empty;

    /// <summary>
    ///     UTC time the indexes were last built or rebuilt.
    /// </summary>
    public DateTime LastBuiltUtc { get; init; }
}
