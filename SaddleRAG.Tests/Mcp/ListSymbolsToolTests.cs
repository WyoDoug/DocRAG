// ListSymbolsToolTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;
using NSubstitute;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class ListSymbolsToolTests
{
    [Fact]
    public async Task ListSymbolsClassKindReturnsClassesOnly()
    {
        var (factory, libraryRepo, chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        chunkRepo.GetSymbolsAsync("foo", "1.0", SymbolKind.Type, null, Arg.Any<CancellationToken>())
                 .Returns(new[] { "ClassA", "ClassB" });

        var json = await LibraryTools.ListSymbols(factory, library: "foo", kind: "class",
                                                  ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"ClassA\"", json);
        Assert.Contains("\"class\"", json);
    }

    [Fact]
    public async Task ListSymbolsNullKindReturnsAllKindsTagged()
    {
        var (factory, libraryRepo, chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        chunkRepo.GetAllSymbolsAsync("foo", "1.0", null, Arg.Any<CancellationToken>())
                 .Returns(new[]
                              {
                                  new Symbol { Name = "ClassA", Kind = SymbolKind.Type },
                                  new Symbol { Name = "FuncB", Kind = SymbolKind.Function }
                              });

        var json = await LibraryTools.ListSymbols(factory, library: "foo", kind: null,
                                                  ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"ClassA\"", json);
        Assert.Contains("\"FuncB\"", json);
        Assert.Contains("\"class\"", json);
        Assert.Contains("\"function\"", json);
    }

    [Fact]
    public async Task ListSymbolsNotFoundReturnsErrorJson()
    {
        var (factory, libraryRepo, _) = MakeFactory();
        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>()).Returns((LibraryRecord?) null);

        var json = await LibraryTools.ListSymbols(factory, library: "missing", kind: "class",
                                                  ct: TestContext.Current.CancellationToken);

        Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
    }

    private static (RepositoryFactory factory, ILibraryRepository libraryRepo, IChunkRepository chunkRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        return (factory, libraryRepo, chunkRepo);
    }
}
