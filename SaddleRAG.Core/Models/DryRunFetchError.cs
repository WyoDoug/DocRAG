// DryRunFetchError.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A page that failed to fetch during a dry run.
/// </summary>
public record DryRunFetchError
{
    public required string Url { get; init; }
    public required int HttpStatus { get; init; }
    public required string ErrorKind { get; init; }
    public required string Message { get; init; }
}
