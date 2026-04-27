// Bm25ShardRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.IO.Compression;
using System.Text.Json;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB-backed implementation of <see cref="IBm25ShardRepository"/>.
///     Persists sharded BM25 postings with two levels of overflow defense:
///
///       1. Per-term spill: any term whose postings list serializes above
///          <see cref="PerTermSpillThresholdBytes"/> is uploaded to GridFS
///          and replaced with a file-id reference in the shard document.
///       2. Per-shard spill: if a shard document still serializes above
///          <see cref="PerShardSpillThresholdBytes"/> after per-term spill
///          (very unusual — would require thousands of medium-frequency
///          terms in one shard), the entire shard payload is uploaded to
///          GridFS and the document keeps only the file id.
///
///     Spilled payloads are stored under a deterministic GridFS filename
///     keyed by (libraryId, version, shard, term?) so re-writes overwrite
///     prior content rather than accumulate orphans, and so a delete of
///     the (libraryId, version) cleans up all related GridFS files.
/// </summary>
public class Bm25ShardRepository : IBm25ShardRepository
{
    public Bm25ShardRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task ReplaceShardsAsync(string libraryId,
                                         string version,
                                         IReadOnlyList<Bm25Shard> shards,
                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(shards);

        await DeleteShardsAsync(libraryId, version, ct);

        if (shards.Count > 0)
        {
            var bucket = mContext.Bm25Bucket;
            var collection = mContext.Bm25Shards;

            var persistedShards = new List<Bm25Shard>(shards.Count);
            foreach(var shard in shards)
            {
                var prepared = await PrepareForPersistAsync(shard, bucket, ct);
                persistedShards.Add(prepared);
            }

            await collection.InsertManyAsync(persistedShards, cancellationToken: ct);
        }
    }

    /// <inheritdoc />
    public async Task<Bm25Shard?> GetShardAsync(string libraryId,
                                                string version,
                                                int shardIndex,
                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeShardId(libraryId, version, shardIndex);
        var result = await mContext.Bm25Shards
                                   .Find(s => s.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bm25Shard>> GetAllShardsAsync(string libraryId,
                                                                  string version,
                                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var found = await mContext.Bm25Shards
                                  .Find(s => s.LibraryId == libraryId && s.Version == version)
                                  .ToListAsync(ct);
        return found;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bm25Posting>> LoadPostingsAsync(Bm25Shard shard,
                                                                    string term,
                                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentException.ThrowIfNullOrEmpty(term);

        IReadOnlyList<Bm25Posting> result;
        if (shard.ShardGridFsRef != null)
            result = await ResolveFromShardSpillAsync(shard.ShardGridFsRef, term, ct);
        else
            result = await ResolveFromInlineOrTermSpillAsync(shard, term, ct);
        return result;
    }

    private async Task<IReadOnlyList<Bm25Posting>> ResolveFromShardSpillAsync(string shardFileId,
                                                                              string term,
                                                                              CancellationToken ct)
    {
        var allTerms = await DownloadShardPayloadAsync(shardFileId, ct);
        var result = allTerms.TryGetValue(term, out var found) ? found : [];
        return result;
    }

    private async Task<IReadOnlyList<Bm25Posting>> ResolveFromInlineOrTermSpillAsync(Bm25Shard shard,
                                                                                     string term,
                                                                                     CancellationToken ct)
    {
        IReadOnlyList<Bm25Posting>? result = null;
        if (shard.InlineTerms.TryGetValue(term, out var inline))
            result = inline;
        if (result == null && shard.ExternalTerms.TryGetValue(term, out var fileId))
            result = await DownloadTermPayloadAsync(fileId, ct);
        return result ?? Array.Empty<Bm25Posting>();
    }

    /// <inheritdoc />
    public async Task DeleteShardsAsync(string libraryId,
                                        string version,
                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var existing = await GetAllShardsAsync(libraryId, version, ct);
        await mContext.Bm25Shards
                      .DeleteManyAsync(s => s.LibraryId == libraryId && s.Version == version, ct);

        var bucket = mContext.Bm25Bucket;
        foreach(var shard in existing)
            await DeleteSpilledPayloadsAsync(shard, bucket, ct);
    }

    /// <summary>
    ///     Apply per-term and per-shard spill rules and produce a shard
    ///     ready to insert. Pure persistence concern — the builder
    ///     produces shards with everything inline; this layer decides
    ///     what spills based on serialized size.
    /// </summary>
    private static async Task<Bm25Shard> PrepareForPersistAsync(Bm25Shard shard,
                                                                IGridFSBucket bucket,
                                                                CancellationToken ct)
    {
        var inline = new Dictionary<string, IReadOnlyList<Bm25Posting>>(shard.InlineTerms, StringComparer.Ordinal);
        var external = new Dictionary<string, string>(shard.ExternalTerms, StringComparer.Ordinal);

        await SpillFatTermsAsync(inline, external, shard, bucket, ct);

        var trial = shard with { InlineTerms = inline, ExternalTerms = external };
        var serialized = SerializeShardForSizeCheck(trial);
        var result = serialized.Length <= PerShardSpillThresholdBytes
                         ? trial
                         : await SpillEntireShardAsync(shard, inline, bucket, ct);
        return result;
    }

    private static async Task SpillFatTermsAsync(Dictionary<string, IReadOnlyList<Bm25Posting>> inline,
                                                 Dictionary<string, string> external,
                                                 Bm25Shard shard,
                                                 IGridFSBucket bucket,
                                                 CancellationToken ct)
    {
        var fatTerms = inline.Where(kv => EstimatePostingsSize(kv.Value) > PerTermSpillThresholdBytes)
                             .Select(kv => kv.Key)
                             .ToList();
        foreach(var term in fatTerms)
        {
            var fileName = MakeTermFileName(shard.LibraryId, shard.Version, shard.ShardIndex, term);
            var bytes = SerializePostings(inline[term]);
            var fileId = await UploadAsync(bucket, fileName, bytes, ct);
            external[term] = fileId.ToString();
            inline.Remove(term);
        }
    }

    private static async Task<Bm25Shard> SpillEntireShardAsync(Bm25Shard original,
                                                               Dictionary<string, IReadOnlyList<Bm25Posting>> inline,
                                                               IGridFSBucket bucket,
                                                               CancellationToken ct)
    {
        var fileName = MakeShardFileName(original.LibraryId, original.Version, original.ShardIndex);
        var bytes = SerializePostingsDictionary(inline);
        var fileId = await UploadAsync(bucket, fileName, bytes, ct);
        var result = original with
                         {
                             InlineTerms = new Dictionary<string, IReadOnlyList<Bm25Posting>>(),
                             ExternalTerms = new Dictionary<string, string>(),
                             ShardGridFsRef = fileId.ToString()
                         };
        return result;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<Bm25Posting>>> DownloadShardPayloadAsync(
        string fileIdString,
        CancellationToken ct)
    {
        var bytes = await DownloadAsync(mContext.Bm25Bucket, fileIdString, ct);
        var result = DeserializePostingsDictionary(bytes);
        return result;
    }

    private async Task<IReadOnlyList<Bm25Posting>> DownloadTermPayloadAsync(string fileIdString,
                                                                            CancellationToken ct)
    {
        var bytes = await DownloadAsync(mContext.Bm25Bucket, fileIdString, ct);
        var result = DeserializePostings(bytes);
        return result;
    }

    private static async Task<ObjectId> UploadAsync(IGridFSBucket bucket,
                                                    string fileName,
                                                    byte[] bytes,
                                                    CancellationToken ct)
    {
        // Overwrite by deleting any prior file with the same logical name first.
        // GridFS treats names as non-unique; without this we accumulate orphans
        // across rebuilds.
        await DeleteByNameAsync(bucket, fileName, ct);
        var result = await bucket.UploadFromBytesAsync(fileName, bytes, cancellationToken: ct);
        return result;
    }

    private static async Task<byte[]> DownloadAsync(IGridFSBucket bucket, string fileIdString, CancellationToken ct)
    {
        var fileId = ObjectId.Parse(fileIdString);
        var result = await bucket.DownloadAsBytesAsync(fileId, cancellationToken: ct);
        return result;
    }

    private static async Task DeleteByNameAsync(IGridFSBucket bucket, string fileName, CancellationToken ct)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq(f => f.Filename, fileName);
        using var cursor = await bucket.FindAsync(filter, cancellationToken: ct);
        var matches = await cursor.ToListAsync(ct);
        foreach(var match in matches)
            await bucket.DeleteAsync(match.Id, ct);
    }

    private static async Task DeleteSpilledPayloadsAsync(Bm25Shard shard, IGridFSBucket bucket, CancellationToken ct)
    {
        if (shard.ShardGridFsRef != null)
            await TryDeleteByIdAsync(bucket, shard.ShardGridFsRef, ct);
        foreach(var fileIdString in shard.ExternalTerms.Values)
            await TryDeleteByIdAsync(bucket, fileIdString, ct);
    }

    private static async Task TryDeleteByIdAsync(IGridFSBucket bucket, string fileIdString, CancellationToken ct)
    {
        var parsed = ObjectId.TryParse(fileIdString, out var fileId);
        if (parsed)
        {
            try
            {
                await bucket.DeleteAsync(fileId, ct);
            }
            catch(GridFSFileNotFoundException)
            {
                // Already gone — fine, target state achieved.
            }
        }
    }

    /// <summary>
    ///     Estimate serialized size of a postings list without doing a full
    ///     BSON round-trip. Each Bm25Posting is roughly: ChunkId.Length +
    ///     12 bytes overhead. Conservative — overestimates slightly so we
    ///     spill a hair earlier than strictly needed.
    /// </summary>
    private static int EstimatePostingsSize(IReadOnlyList<Bm25Posting> postings) =>
        postings.Sum(p => p.ChunkId.Length + PerPostingOverheadBytes);

    private static byte[] SerializePostings(IReadOnlyList<Bm25Posting> postings) =>
        Compress(JsonSerializer.SerializeToUtf8Bytes(postings, smJsonOptions));

    private static IReadOnlyList<Bm25Posting> DeserializePostings(byte[] bytes) =>
        JsonSerializer.Deserialize<List<Bm25Posting>>(Decompress(bytes), smJsonOptions) ?? [];

    private static byte[] SerializePostingsDictionary(IReadOnlyDictionary<string, IReadOnlyList<Bm25Posting>> dict) =>
        Compress(JsonSerializer.SerializeToUtf8Bytes(dict, smJsonOptions));

    private static IReadOnlyDictionary<string, IReadOnlyList<Bm25Posting>> DeserializePostingsDictionary(byte[] bytes) =>
        JsonSerializer.Deserialize<Dictionary<string, List<Bm25Posting>>>(Decompress(bytes), smJsonOptions)
            ?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Bm25Posting>) kv.Value, StringComparer.Ordinal)
        ?? new Dictionary<string, IReadOnlyList<Bm25Posting>>();

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using(var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] SerializeShardForSizeCheck(Bm25Shard shard)
    {
        var doc = shard.ToBsonDocument();
        var result = doc.ToBson();
        return result;
    }

    private static string MakeShardFileName(string libraryId, string version, int shardIndex) =>
        $"{libraryId}/{version}/shard-{shardIndex}";

    private static string MakeTermFileName(string libraryId, string version, int shardIndex, string term)
    {
        var sanitized = SanitizeForFileName(term);
        var result = $"{libraryId}/{version}/shard-{shardIndex}/term-{sanitized}";
        return result;
    }

    private static string SanitizeForFileName(string term)
    {
        var chars = term.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
        var result = new string(chars);
        return result;
    }

    public static string MakeShardId(string libraryId, string version, int shardIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var result = $"{libraryId}/{version}/{shardIndex}";
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new()
                                                                      {
                                                                          IncludeFields = false,
                                                                          DefaultIgnoreCondition =
                                                                              System.Text.Json.Serialization.JsonIgnoreCondition.Never
                                                                      };

    // 1 MB per term — terms with postings larger than this spill to GridFS.
    private const int PerTermSpillThresholdBytes = 1 * 1024 * 1024;

    // 14 MB per shard — under Mongo's 16MB cap with safety margin.
    private const int PerShardSpillThresholdBytes = 14 * 1024 * 1024;

    private const int PerPostingOverheadBytes = 12;
}
