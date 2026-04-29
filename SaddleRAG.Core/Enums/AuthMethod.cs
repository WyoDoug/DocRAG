// AuthMethod.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Authentication method for scraping protected documentation sites.
/// </summary>
public enum AuthMethod
{
    /// <summary>
    ///     Inject a pre-obtained cookie string into all requests.
    /// </summary>
    Cookie,

    /// <summary>
    ///     Automate a login form before crawling via Playwright.
    /// </summary>
    LoginForm,

    /// <summary>
    ///     Pass an API key or bearer token in a request header.
    /// </summary>
    ApiKey
}
