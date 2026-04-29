// SuspectReason.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Ingestion.Suspect;

/// <summary>
///     String constants for the SuspectReasons[] surfaced by the
///     post-scrape detector and stored on LibraryVersionRecord.
/// </summary>
public static class SuspectReason
{
    public const string OnePager = "OnePager";
    public const string SparseLinkGraph = "SparseLinkGraph";
    public const string SingleHost = "SingleHost";
    public const string LanguageMismatch = "LanguageMismatch";
    public const string ReadmeOnly = "ReadmeOnly";
}
