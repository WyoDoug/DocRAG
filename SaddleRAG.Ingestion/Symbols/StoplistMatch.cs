// StoplistMatch.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     Result of a profile-aware stoplist check.
/// </summary>
public enum StoplistMatch
{
    /// <summary>Token was not in any stoplist.</summary>
    None,

    /// <summary>Token matched the universal Stoplist.</summary>
    Global,

    /// <summary>Token matched LibraryProfile.Stoplist.</summary>
    Library
}
