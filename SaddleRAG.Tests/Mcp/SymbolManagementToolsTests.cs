// SymbolManagementToolsTests.cs
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

public sealed class SymbolManagementToolsTests
{
    [Fact]
    public async Task ListExcludedSymbolsReturnsRejections()
    {
        var (factory, profileRepo, excludedRepo) = MakeFactory();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile([], []));
        excludedRepo.ListAsync("lib", "1.0", null, 50, Arg.Any<CancellationToken>())
                    .Returns(new[]
                                 {
                                     MakeExcluded("along", SymbolRejectionReason.NoStructureSignal, chunkCount: 47),
                                     MakeExcluded("data", SymbolRejectionReason.NoStructureSignal, chunkCount: 32)
                                 });
        excludedRepo.CountAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(2);

        var json = await SymbolManagementTools.ListExcludedSymbols(factory,
                                                                    "lib",
                                                                    "1.0",
                                                                    reason: null,
                                                                    limit: 50,
                                                                    profile: null,
                                                                    TestContext.Current.CancellationToken);

        Assert.Contains("along", json);
        Assert.Contains("NoStructureSignal", json);
    }

    [Fact]
    public async Task ListExcludedSymbolsReturnsReconNeededWhenProfileMissing()
    {
        var (factory, profileRepo, _) = MakeFactory();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryProfile?) null);

        var json = await SymbolManagementTools.ListExcludedSymbols(factory,
                                                                    "lib",
                                                                    "1.0",
                                                                    reason: null,
                                                                    limit: 50,
                                                                    profile: null,
                                                                    TestContext.Current.CancellationToken);

        Assert.Contains("ReconNeeded", json);
    }

    [Fact]
    public async Task AddToLikelySymbolsPromotesAndRemovesFromStoplist()
    {
        var (factory, profileRepo, excludedRepo) = MakeFactory();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile(likelySymbols: ["existing"], stoplist: ["foo", "bar"]));

        var json = await SymbolManagementTools.AddToLikelySymbols(factory,
                                                                   "lib",
                                                                   "1.0",
                                                                   names: ["foo", "newone"],
                                                                   profile: null,
                                                                   TestContext.Current.CancellationToken);

        Assert.Contains("\"foo\"", json);
        Assert.Contains("RemovedFromStoplist", json);
        await profileRepo.Received(1).UpsertAsync(Arg.Is<LibraryProfile>(p => p.LikelySymbols.Contains("foo")
                                                                              && p.LikelySymbols.Contains("newone")
                                                                              && p.LikelySymbols.Contains("existing")
                                                                              && !p.Stoplist.Contains("foo")
                                                                              && p.Stoplist.Contains("bar")),
                                                  Arg.Any<CancellationToken>());
        await excludedRepo.Received(1).RemoveAsync("lib", "1.0", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddToStoplistDemotesAndRemovesFromLikelySymbols()
    {
        var (factory, profileRepo, excludedRepo) = MakeFactory();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile(likelySymbols: ["foo", "bar"], stoplist: ["existing"]));

        var json = await SymbolManagementTools.AddToStoplist(factory,
                                                              "lib",
                                                              "1.0",
                                                              names: ["foo", "newnoise"],
                                                              profile: null,
                                                              TestContext.Current.CancellationToken);

        Assert.Contains("\"foo\"", json);
        Assert.Contains("RemovedFromLikelySymbols", json);
        await profileRepo.Received(1).UpsertAsync(Arg.Is<LibraryProfile>(p => p.Stoplist.Contains("foo")
                                                                              && p.Stoplist.Contains("newnoise")
                                                                              && p.Stoplist.Contains("existing")
                                                                              && !p.LikelySymbols.Contains("foo")
                                                                              && p.LikelySymbols.Contains("bar")),
                                                  Arg.Any<CancellationToken>());
        await excludedRepo.Received(1).RemoveAsync("lib", "1.0", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddToLikelySymbolsThrowsOnEmptyNames()
    {
        var (factory, _, _) = MakeFactory();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await SymbolManagementTools.AddToLikelySymbols(factory,
                                                           "lib",
                                                           "1.0",
                                                           names: [],
                                                           profile: null,
                                                           TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddToStoplistThrowsOnEmptyNames()
    {
        var (factory, _, _) = MakeFactory();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await SymbolManagementTools.AddToStoplist(factory,
                                                      "lib",
                                                      "1.0",
                                                      names: [],
                                                      profile: null,
                                                      TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddToStoplistRemovesCaseEquivalentFromLikelySymbols()
    {
        // Override semantics: adding "foo" to stoplist should remove
        // "Foo" from LikelySymbols (case-insensitive subtraction).
        var (factory, profileRepo, _) = MakeFactory();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile(likelySymbols: ["Foo"], stoplist: []));

        await SymbolManagementTools.AddToStoplist(factory,
                                                  "lib",
                                                  "1.0",
                                                  names: ["foo"],
                                                  profile: null,
                                                  TestContext.Current.CancellationToken);

        await profileRepo.Received(1).UpsertAsync(Arg.Is<LibraryProfile>(p => !p.LikelySymbols.Contains("Foo")),
                                                  Arg.Any<CancellationToken>());
    }

    private static (RepositoryFactory factory, ILibraryProfileRepository profileRepo, IExcludedSymbolsRepository excludedRepo) MakeFactory()
    {
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);
        return (factory, profileRepo, excludedRepo);
    }

    private static LibraryProfile MakeProfile(IReadOnlyList<string> likelySymbols, IReadOnlyList<string> stoplist) =>
        new()
            {
                Id = "lib/1.0",
                LibraryId = "lib",
                Version = "1.0",
                Source = "test",
                LikelySymbols = likelySymbols,
                Stoplist = stoplist
            };

    private static ExcludedSymbol MakeExcluded(string name, SymbolRejectionReason reason, int chunkCount) =>
        new()
            {
                Id = ExcludedSymbol.MakeId("lib", "1.0", name),
                LibraryId = "lib",
                Version = "1.0",
                Name = name,
                Reason = reason,
                SampleSentences = ["sample one", "sample two"],
                ChunkCount = chunkCount,
                CapturedUtc = DateTime.UtcNow
            };
}
