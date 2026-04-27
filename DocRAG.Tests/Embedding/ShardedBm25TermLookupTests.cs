// // ShardedBm25TermLookupTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Embedding;

public sealed class ShardedBm25TermLookupTests
{
    [Fact]
    public async Task PreloadFetchesOnlyShardsForSuppliedTerms()
    {
        var fake = BuildFakeRepoWithFiveShards(out var allShards);
        const int ShardCount = 5;
        var lookup = new ShardedBm25TermLookup(fake, "lib", "1.0", ShardCount);

        var hotTerm = allShards[2].InlineTerms.Keys.First();
        await lookup.PreloadAsync([hotTerm]);

        Assert.Single(fake.LoadedShardIndexes);
        Assert.Contains(Bm25IndexBuilder.ShardIndexFor(hotTerm, ShardCount), fake.LoadedShardIndexes);
    }

    [Fact]
    public async Task PreloadIsIdempotent()
    {
        var fake = BuildFakeRepoWithFiveShards(out var allShards);
        const int ShardCount = 5;
        var lookup = new ShardedBm25TermLookup(fake, "lib", "1.0", ShardCount);

        var term = allShards[1].InlineTerms.Keys.First();
        await lookup.PreloadAsync([term]);
        await lookup.PreloadAsync([term]);

        Assert.Single(fake.LoadedShardIndexes);
    }

    [Fact]
    public async Task GetPostingsReturnsLoadedTermAfterPreload()
    {
        var fake = BuildFakeRepoWithFiveShards(out var allShards);
        const int ShardCount = 5;
        var lookup = new ShardedBm25TermLookup(fake, "lib", "1.0", ShardCount);

        var (term, expectedPostings) = allShards[3].InlineTerms.First();
        await lookup.PreloadAsync([term]);

        var actualPostings = lookup.GetPostings(term);
        Assert.Equal(expectedPostings.Count, actualPostings.Count);
    }

    [Fact]
    public async Task GetPostingsReturnsEmptyForUnknownTerm()
    {
        var fake = BuildFakeRepoWithFiveShards(out _);
        const int ShardCount = 5;
        var lookup = new ShardedBm25TermLookup(fake, "lib", "1.0", ShardCount);

        await lookup.PreloadAsync(["nonexistent_xyz_term"]);
        var postings = lookup.GetPostings("nonexistent_xyz_term");

        Assert.Empty(postings);
    }

    private static FakeBm25ShardRepository BuildFakeRepoWithFiveShards(out IReadOnlyList<Bm25Shard> shards)
    {
        var chunks = new[]
        {
            MakeChunk("a", "alpha bravo charlie delta echo foxtrot golf hotel"),
            MakeChunk("b", "india juliet kilo lima mike november oscar papa"),
            MakeChunk("c", "quebec romeo sierra tango uniform victor whiskey xray"),
            MakeChunk("d", "yankee zulu alfa bravo charlie sierra")
        };
        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks, shardCount: 5);
        shards = build.Shards;
        return new FakeBm25ShardRepository(build.Shards);
    }

    private static DocChunk MakeChunk(string id, string content) =>
        new()
        {
            Id = id,
            LibraryId = "lib",
            Version = "1.0",
            PageUrl = "https://example.com",
            PageTitle = "Page",
            Category = DocRAG.Core.Enums.DocCategory.ApiReference,
            Content = content
        };

    private sealed class FakeBm25ShardRepository : IBm25ShardRepository
    {
        public FakeBm25ShardRepository(IReadOnlyList<Bm25Shard> shards)
        {
            mShardsByIndex = shards.ToDictionary(s => s.ShardIndex);
        }

        private readonly Dictionary<int, Bm25Shard> mShardsByIndex;
        public List<int> LoadedShardIndexes { get; } = [];

        public Task<Bm25Shard?> GetShardAsync(string libraryId, string version, int shardIndex, CancellationToken ct = default)
        {
            LoadedShardIndexes.Add(shardIndex);
            mShardsByIndex.TryGetValue(shardIndex, out var shard);
            return Task.FromResult(shard);
        }

        public Task<IReadOnlyList<Bm25Posting>> LoadPostingsAsync(Bm25Shard shard, string term, CancellationToken ct = default)
        {
            var result = shard.InlineTerms.TryGetValue(term, out var found) ? found : [];
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<Bm25Shard>> GetAllShardsAsync(string libraryId, string version, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bm25Shard>>(mShardsByIndex.Values.ToList());

        public Task ReplaceShardsAsync(string libraryId, string version, IReadOnlyList<Bm25Shard> shards, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteShardsAsync(string libraryId, string version, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
