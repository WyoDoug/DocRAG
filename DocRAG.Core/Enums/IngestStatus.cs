// IngestStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     State machine for the start_ingest entrypoint. Each value tells the
///     calling LLM what tool to invoke next.
/// </summary>
public enum IngestStatus
{
    /// <summary>
    ///     No LibraryProfile cached for the (library, version). Caller should
    ///     run reconnaissance and call submit_library_profile before retrying.
    /// </summary>
    ReconNeeded,

    /// <summary>
    ///     Profile cached, no chunks indexed. Caller should call scrape_docs.
    /// </summary>
    ReadyToScrape,

    /// <summary>
    ///     Some chunks indexed but the scrape was incomplete (MaxPages or
    ///     interrupted). Caller should call continue_scrape.
    /// </summary>
    Partial,

    /// <summary>
    ///     Chunks present but were extracted with an older parser version.
    ///     Caller should call rescrub_library.
    /// </summary>
    Stale,

    /// <summary>
    ///     URL maps to a known library but a new version. Caller decides:
    ///     ingest as new version, or alias to existing.
    /// </summary>
    VersionDrift,

    /// <summary>
    ///     Profile + full index + current parser version. Caller can query.
    /// </summary>
    Ready
}
