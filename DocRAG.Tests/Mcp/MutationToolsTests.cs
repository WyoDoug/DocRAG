// MutationToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Mcp.Tools;
using NSubstitute;

#endregion

namespace DocRAG.Tests.Mcp;

public sealed class MutationToolsTests
{
    [Fact]
    public async Task RenameLibraryDryRunReportsOutcomeWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.GetLibraryAsync("old", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "old",
                                    Name = "old",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        libraryRepo.GetLibraryAsync("new", Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"Renamed\"", json);
        await libraryRepo.DidNotReceive().RenameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryDryRunReportsNotFoundWhenMissing()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     library: "missing",
                                                     newId: "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"NotFound\"", json);
    }

    [Fact]
    public async Task RenameLibraryApplyCallsRepoOnce()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                                     new RenameLibraryResult(1, 1, 100, 50, 1, 1, 1, 5, 3)));

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"Renamed\"", json);
        await libraryRepo.Received(1).RenameAsync("old", "new", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryCollisionReportsAndDoesNotApply()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Collision, null));

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"Collision\"", json);
    }
}
