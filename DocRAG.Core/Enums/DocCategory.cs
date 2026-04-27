// DocCategory.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Classification category for a scraped documentation page.
/// </summary>
public enum DocCategory
{
    /// <summary>
    ///     Conceptual overview, architecture explanation, "about" pages.
    /// </summary>
    Overview,

    /// <summary>
    ///     Step-by-step guide, tutorial, "how to do X" content.
    /// </summary>
    HowTo,

    /// <summary>
    ///     Code samples, demos, example projects.
    /// </summary>
    Sample,

    /// <summary>
    ///     Source code â€” library implementation files (not usage examples).
    /// </summary>
    Code,

    /// <summary>
    ///     API reference â€” class, method, property, event documentation.
    /// </summary>
    ApiReference,

    /// <summary>
    ///     Release notes, migration guides, changelog.
    /// </summary>
    ChangeLog,

    /// <summary>
    ///     Did not fit other categories or could not be classified.
    /// </summary>
    Unclassified
}
