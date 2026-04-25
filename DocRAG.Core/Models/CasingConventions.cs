// // CasingConventions.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Naming-convention hints supplied by recon. Each field names the
///     casing style used by that category of identifier in the docs site
///     (for example "PascalCase", "camelCase", "snake_case",
///     "SCREAMING_SNAKE", "kebab-case"). String-typed rather than enum so
///     recon can express styles we have not enumerated.
/// </summary>
public record CasingConventions
{
    /// <summary>
    ///     Convention used for type names (classes, structs, enums).
    /// </summary>
    public string Types { get; init; } = string.Empty;

    /// <summary>
    ///     Convention used for method/function names.
    /// </summary>
    public string Methods { get; init; } = string.Empty;

    /// <summary>
    ///     Convention used for constants.
    /// </summary>
    public string Constants { get; init; } = string.Empty;

    /// <summary>
    ///     Convention used for properties / fields / members.
    /// </summary>
    public string Members { get; init; } = string.Empty;

    /// <summary>
    ///     Convention used for parameters and arguments.
    /// </summary>
    public string Parameters { get; init; } = string.Empty;
}
