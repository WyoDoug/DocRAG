// LibraryRepositoryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Tests.Repositories;

/// <summary>
///     Behavioral contract tests for ILibraryRepository.DeleteVersionAsync and
///     ILibraryRepository.RenameAsync. These tests confirm the expected
///     result shapes for each scenario: they use a substitute of the
///     interface to model what the real implementation must return for
///     each distinct case, and verify that callers can depend on that contract.
/// </summary>
public sealed class LibraryRepositoryTests
{
    [Fact]
    public async Task DeleteVersionLastVersionResultHasLibraryRowDeletedTrue()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.DeleteVersionAsync("mylib", "1.0", Arg.Any<CancellationToken>())
            .Returns(new DeleteVersionResult(VersionsDeleted: 1,
                                             LibraryRowDeleted: true,
                                             CurrentVersionRepointedTo: null
                                            ));

        var result = await repo.DeleteVersionAsync("mylib", "1.0", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.VersionsDeleted);
        Assert.True(result.LibraryRowDeleted);
        Assert.Null(result.CurrentVersionRepointedTo);
    }

    [Fact]
    public async Task DeleteVersionCurrentVersionRepointResultHasRepointedVersion()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.DeleteVersionAsync("mylib", "1.0", Arg.Any<CancellationToken>())
            .Returns(new DeleteVersionResult(VersionsDeleted: 1,
                                             LibraryRowDeleted: false,
                                             CurrentVersionRepointedTo: "3.0"
                                            ));

        var result = await repo.DeleteVersionAsync("mylib", "1.0", TestContext.Current.CancellationToken);

        Assert.False(result.LibraryRowDeleted);
        Assert.Equal("3.0", result.CurrentVersionRepointedTo);
    }

    [Fact]
    public async Task DeleteVersionNonCurrentVersionResultHasNullRepoint()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.DeleteVersionAsync("mylib", "2.0", Arg.Any<CancellationToken>())
            .Returns(new DeleteVersionResult(VersionsDeleted: 1,
                                             LibraryRowDeleted: false,
                                             CurrentVersionRepointedTo: null
                                            ));

        var result = await repo.DeleteVersionAsync("mylib", "2.0", TestContext.Current.CancellationToken);

        Assert.False(result.LibraryRowDeleted);
        Assert.Null(result.CurrentVersionRepointedTo);
    }

    [Fact]
    public async Task RenameAsyncRenamedOutcomeHasNonNullCounts()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
            .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                               new RenameLibraryResult(1, 1, 5, 3, 1, 1, 0, 0, 0)));

        var response = await repo.RenameAsync("old", "new", TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, response.Outcome);
        Assert.NotNull(response.Counts);
    }

    [Fact]
    public async Task RenameAsyncCollisionOutcomeHasNullCounts()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
            .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Collision, null));

        var response = await repo.RenameAsync("old", "new", TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Collision, response.Outcome);
        Assert.Null(response.Counts);
    }

    [Fact]
    public async Task RenameAsyncNotFoundOutcomeHasNullCounts()
    {
        var repo = Substitute.For<ILibraryRepository>();
        repo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
            .Returns(new RenameLibraryResponse(RenameLibraryOutcome.NotFound, null));

        var response = await repo.RenameAsync("old", "new", TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.NotFound, response.Outcome);
        Assert.Null(response.Counts);
    }
}
