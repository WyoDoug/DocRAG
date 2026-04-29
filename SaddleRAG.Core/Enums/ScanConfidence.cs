// ScanConfidence.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Confidence level for an auto-generated ScrapeJob candidate.
/// </summary>
public enum ScanConfidence
{
    /// <summary>
    ///     ProjectUrl points directly to a documentation site with
    ///     a clear structure. AllowedUrlPatterns are likely correct.
    /// </summary>
    High,

    /// <summary>
    ///     ProjectUrl points to a GitHub repo or general product page.
    ///     Human should verify the docs root URL.
    /// </summary>
    Medium,

    /// <summary>
    ///     No ProjectUrl or it points to something unhelpful.
    ///     Human must provide the docs URL manually.
    /// </summary>
    Low
}
