// ScrapeAuthentication.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     Authentication configuration for scraping protected doc sites.
/// </summary>
public record ScrapeAuthentication
{
    /// <summary>
    ///     Authentication method to use.
    /// </summary>
    public required AuthMethod Method { get; init; }

    /// <summary>
    ///     For Cookie: cookie string. For LoginForm: login page URL.
    ///     For ApiKey: header name.
    /// </summary>
    public required string Credential { get; init; }

    /// <summary>
    ///     For LoginForm: CSS selector for username field.
    /// </summary>
    public string? UsernameSelector { get; init; }

    /// <summary>
    ///     For LoginForm: CSS selector for password field.
    /// </summary>
    public string? PasswordSelector { get; init; }

    /// <summary>
    ///     For LoginForm: CSS selector for submit button.
    /// </summary>
    public string? SubmitSelector { get; init; }

    /// <summary>
    ///     Secret store key name for username.
    /// </summary>
    public string? UsernameSecretKey { get; init; }

    /// <summary>
    ///     Secret store key name for password.
    /// </summary>
    public string? PasswordSecretKey { get; init; }
}
