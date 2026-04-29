// RenameLibraryResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Response from ILibraryRepository.RenameAsync. Counts is null
///     when Outcome is Collision or NotFound.
/// </summary>
public sealed record RenameLibraryResponse(RenameLibraryOutcome Outcome,
                                           RenameLibraryResult? Counts);
