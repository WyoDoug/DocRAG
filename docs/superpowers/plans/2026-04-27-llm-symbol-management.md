# LLM Symbol-Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-library MCP tools (`list_excluded_symbols`, `add_to_likely_symbols`, `add_to_stoplist`) plus a `library_excluded_symbols` Mongo collection so a calling LLM can review/refine extractor decisions for documentation libraries.

**Architecture:** `SymbolExtractor` returns rejection records alongside kept symbols. `RescrubService` aggregates rejections per token (reason + 3 spread samples + chunk count) and persists to a new collection. Three new MCP tools mutate `LibraryProfile.LikelySymbols` / `LibraryProfile.Stoplist` and emit hints suggesting `rescrub_library`. The hint is conditional on excluded-count thresholds so quiet libraries stay quiet.

**Tech Stack:** C# .NET 9, MongoDB (via MongoDB.Driver), xUnit + NSubstitute for tests, ModelContextProtocol SDK for the MCP server.

**Spec:** [docs/superpowers/specs/2026-04-27-llm-symbol-management-design.md](../specs/2026-04-27-llm-symbol-management-design.md)

---

## File Structure

| File | Purpose | Action |
|---|---|---|
| `DocRAG.Core/Enums/SymbolRejectionReason.cs` | 6-value enum naming each reject path | **Create** |
| `DocRAG.Core/Models/ExcludedSymbol.cs` | Persistable record for a rejected token + samples | **Create** |
| `DocRAG.Core/Models/LibraryProfile.cs` | Add `Stoplist` field, bump `CurrentSchemaVersion` to 2 | **Modify** |
| `DocRAG.Core/Models/RescrubResult.cs` | Add `ExcludedCount` and `Hints` fields | **Modify** |
| `DocRAG.Core/Interfaces/IExcludedSymbolsRepository.cs` | Repository contract | **Create** |
| `DocRAG.Database/DocRagDbContext.cs` | Register `ExcludedSymbols` collection + indexes | **Modify** |
| `DocRAG.Database/Repositories/ExcludedSymbolsRepository.cs` | Mongo impl of the repo | **Create** |
| `DocRAG.Database/Repositories/RepositoryFactory.cs` | Add `GetExcludedSymbolsRepository(profile)` | **Modify** |
| `DocRAG.Database/ServiceCollectionExtensions.cs` | DI registration for the new repo | **Modify** |
| `DocRAG.Ingestion/Symbols/Stoplist.cs` | Add `StoplistMatch` enum + profile-aware `Match` overload | **Modify** |
| `DocRAG.Ingestion/Symbols/RejectedToken.cs` | Per-rejection record returned by extractor | **Create** |
| `DocRAG.Ingestion/Symbols/ExtractedSymbols.cs` | Add `Rejected` field | **Modify** |
| `DocRAG.Ingestion/Symbols/SymbolExtractor.cs` | Return rejections; `IsAdmissible` becomes reason-returning | **Modify** |
| `DocRAG.Ingestion/Symbols/SampleWindowExtractor.cs` | 200-char window helper | **Create** |
| `DocRAG.Ingestion/Recon/RejectionAccumulator.cs` | Aggregates rejections across chunks (reason, count, thirds-bucketed samples) | **Create** |
| `DocRAG.Ingestion/Recon/RescrubService.cs` | Wire accumulator + hints; takes excluded repo | **Modify** |
| `DocRAG.Ingestion/Recon/LibraryProfileService.cs` | Carry forward `Stoplist` from prior version when target's stoplist is empty | **Modify** |
| `DocRAG.Mcp/Tools/SymbolManagementTools.cs` | The three new MCP tools | **Create** |
| `DocRAG.Mcp/Tools/RescrubTools.cs` | None — `RescrubResult` shape change rides through serializer | (no change) |
| `DocRAG.Tests/Symbols/SymbolExtractorTests.cs` | Add reason-mapping cases | **Modify** |
| `DocRAG.Tests/Symbols/StoplistTests.cs` | New file covering profile-aware match | **Create** |
| `DocRAG.Tests/Symbols/SampleWindowExtractorTests.cs` | New tests | **Create** |
| `DocRAG.Tests/Recon/RejectionAccumulatorTests.cs` | New tests | **Create** |
| `DocRAG.Tests/Recon/RescrubServiceTests.cs` | Add hint-threshold + excluded-persist tests | **Modify** |
| `DocRAG.Tests/Recon/LibraryProfileServiceTests.cs` | Add Stoplist carry-forward tests | **Modify** |
| `DocRAG.Tests/Mcp/SymbolManagementToolsTests.cs` | New tests for the 3 MCP tools | **Create** |

## Standing Conventions (apply to every task)

- Allman braces. Single-return per method (use a result variable). No early returns. No `if/else if` chains — use `switch` expressions or boolean composition. No `continue` — filter via `Where()` or use if-blocks. Max 3 nesting levels.
- Field prefixes: `m` (private instance), `sm` (private static readonly), `ps` (private static), `pm` (public instance).
- Comments on their own line. XML docs on every public member. File header on every new file:
  ```csharp
  // // <FileName>.cs
  // // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
  // // Use subject to the MIT License.
  ```
- File order: enums → delegates → nested types → constructors → properties → fields (readonly first) → interface impls → other members → static fields/constants.
- Logging: `private static IPenskeLogger Log { get; } = LoggingManager.GetLog<ClassName>();` — but DocRAG uses `ILogger<T>` from `Microsoft.Extensions.Logging` per existing patterns. Match what's already in the file.
- All git commits via `git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt` (message file, never inline). NEVER add `Co-Authored-By:` trailers or "🤖 Generated with Claude Code". Body is the user's content only.
- Build verification command: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
- Test command (whole suite): `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --no-build -v minimal`
- Test command (single class): add `--filter "FullyQualifiedName~TestClassName"` to the above.

---

## Task 1: New enum + record (no behavior change yet)

**Files:**
- Create: `DocRAG.Core/Enums/SymbolRejectionReason.cs`
- Create: `DocRAG.Core/Models/ExcludedSymbol.cs`

- [ ] **Step 1.1: Create the enum file**

```csharp
// SymbolRejectionReason.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Why an identifier-shaped token did NOT survive the symbol extractor.
///     Surfaced via the new library_excluded_symbols collection so a calling
///     LLM can triage what was filtered.
/// </summary>
public enum SymbolRejectionReason
{
    /// <summary>
    ///     Hit the universal Stoplist (English stopwords, doc-callout words,
    ///     UI button labels, programming-prose nouns).
    /// </summary>
    GlobalStoplist,

    /// <summary>
    ///     Hit the per-library deny list on LibraryProfile.Stoplist.
    /// </summary>
    LibraryStoplist,

    /// <summary>
    ///     Matched UnitsLookup (mm, GHz, RPM, etc.).
    /// </summary>
    Unit,

    /// <summary>
    ///     Token shorter than the 2-character minimum.
    /// </summary>
    BelowMinLength,

    /// <summary>
    ///     The prose-frequent keep rule was the only path that could have
    ///     saved this token, but the IsLikelyAbbreviation guard blocked it
    ///     (short all-uppercase, looks like an acronym).
    /// </summary>
    LikelyAbbreviation,

    /// <summary>
    ///     Token failed every keep rule in ShouldKeep — no declared form,
    ///     not in LikelySymbols, no code-fence appearance, no container,
    ///     no internal structure, no callable/generic shape, not prose-
    ///     frequent. The catch-all reason for tokens that just don't look
    ///     like symbols.
    /// </summary>
    NoStructureSignal
}
```

- [ ] **Step 1.2: Create the ExcludedSymbol record**

```csharp
// ExcludedSymbol.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     Persistable record describing a token the symbol extractor rejected.
///     Stored in the library_excluded_symbols collection keyed by
///     (LibraryId, Version, Name). Carries the reason plus a few sample
///     sentences so a calling LLM can decide whether the rejection was
///     correct.
/// </summary>
public record ExcludedSymbol
{
    /// <summary>
    ///     Mongo document id. Format: "{LibraryId}/{Version}/{Name}".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Library identifier the rejection applies to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version of the library the rejection applies to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Exact token text (case preserved).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Why the extractor rejected this token.
    /// </summary>
    public required SymbolRejectionReason Reason { get; init; }

    /// <summary>
    ///     Up to three corpus snippets containing this token, drawn from
    ///     different thirds of the chunk stream when possible. Each entry
    ///     is at most 200 characters.
    /// </summary>
    public required IReadOnlyList<string> SampleSentences { get; init; }

    /// <summary>
    ///     Total number of chunks in which the token appeared.
    /// </summary>
    public required int ChunkCount { get; init; }

    /// <summary>
    ///     UTC time the rejection record was captured (last rescrub).
    /// </summary>
    public DateTime CapturedUtc { get; init; }

    /// <summary>
    ///     Compose the document id used as the primary key.
    /// </summary>
    public static string MakeId(string libraryId, string version, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return $"{libraryId}/{version}/{name}";
    }
}
```

- [ ] **Step 1.3: Build to verify the new types compile**

Run: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
Expected: build succeeds, 0 errors.

- [ ] **Step 1.4: Commit**

Write `e:/tmp/msg.txt`:
```
Add SymbolRejectionReason enum and ExcludedSymbol model

Phase 2 scaffolding for the LLM symbol-management flow. New
types are not wired in yet; subsequent commits add the
repository, extractor changes, rescrub plumbing, and MCP tools.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Core/Enums/SymbolRejectionReason.cs DocRAG.Core/Models/ExcludedSymbol.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 2: Add `LibraryProfile.Stoplist` and bump schema version

**Files:**
- Modify: `DocRAG.Core/Models/LibraryProfile.cs`

- [ ] **Step 2.1: Write failing test**

Append to `DocRAG.Tests/Recon/LibraryProfileServiceTests.cs` (top of class, after the existing `BuildPopulatesIdAndCreatedUtc`):

```csharp
[Fact]
public void BuildDefaultsStoplistToEmpty()
{
    var profile = LibraryProfileService.Build("aerotech-aeroscript",
                                              "2025.3",
                                              ["AeroScript"],
                                              new CasingConventions { Types = "PascalCase" },
                                              ["."],
                                              ["Foo()"],
                                              ["MoveLinear"],
                                              canonicalInventoryUrl: null,
                                              confidence: 0.85f,
                                              source: "calling-llm"
                                             );

    Assert.NotNull(profile.Stoplist);
    Assert.Empty(profile.Stoplist);
}

[Fact]
public void CurrentSchemaVersionIsTwoAfterStoplistAddition()
{
    Assert.Equal(2, LibraryProfile.CurrentSchemaVersion);
}
```

- [ ] **Step 2.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~LibraryProfileServiceTests" -v minimal`
Expected: both new tests fail to compile (`Stoplist` doesn't exist) or fail at assertion.

- [ ] **Step 2.3: Edit `LibraryProfile.cs`**

Add the `Stoplist` property after `LikelySymbols` (around line 64) and bump the schema constant (line 91):

Old:
```csharp
    /// <summary>
    ///     Boost set: identifiers recon believes are real types/functions in
    ///     this library. NOT an allowlist — symbols missing from this list
    ///     can still survive extraction via corpus-context rules.
    /// </summary>
    public IReadOnlyList<string> LikelySymbols { get; init; } = [];

    /// <summary>
    ///     URL of a canonical inventory page (for example an enum index
    ///     page) when recon spots one. Optional.
    /// </summary>
    public string? CanonicalInventoryUrl { get; init; }
```

New:
```csharp
    /// <summary>
    ///     Boost set: identifiers recon believes are real types/functions in
    ///     this library. NOT an allowlist — symbols missing from this list
    ///     can still survive extraction via corpus-context rules.
    /// </summary>
    public IReadOnlyList<string> LikelySymbols { get; init; } = [];

    /// <summary>
    ///     Per-library deny list: identifier-shaped tokens that should
    ///     never be classified as symbols for this library, even if they
    ///     would otherwise pass extractor keep rules. Populated by the
    ///     calling LLM via the add_to_stoplist MCP tool. Carried forward
    ///     to new versions of the same library when the new profile's
    ///     stoplist is empty.
    /// </summary>
    public IReadOnlyList<string> Stoplist { get; init; } = [];

    /// <summary>
    ///     URL of a canonical inventory page (for example an enum index
    ///     page) when recon spots one. Optional.
    /// </summary>
    public string? CanonicalInventoryUrl { get; init; }
```

Update the schema-version constant:

Old:
```csharp
    public const int CurrentSchemaVersion = 1;
```

New:
```csharp
    public const int CurrentSchemaVersion = 2;
```

- [ ] **Step 2.4: Run tests to verify they pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~LibraryProfileServiceTests" -v minimal`
Expected: PASS (including pre-existing tests — schema bump should not break `ComputeHash` since hash computation does not include Stoplist yet).

- [ ] **Step 2.5: Commit**

Write `e:/tmp/msg.txt`:
```
Add LibraryProfile.Stoplist field and bump schema version

The per-library stoplist is the deny-side of the new symbol
management flow — the LLM's add_to_stoplist tool writes here.
Existing profiles in Mongo migrate cleanly because BSON auto-map
treats the missing field as the C# default ([]).
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Core/Models/LibraryProfile.cs DocRAG.Tests/Recon/LibraryProfileServiceTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 3: Repository interface

**Files:**
- Create: `DocRAG.Core/Interfaces/IExcludedSymbolsRepository.cs`

- [ ] **Step 3.1: Create the interface**

```csharp
// IExcludedSymbolsRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Persistence surface for per-(library, version) symbol-extractor
///     rejections. Populated by RescrubService at the end of each rescrub;
///     consumed by the SymbolManagement MCP tools.
/// </summary>
public interface IExcludedSymbolsRepository
{
    /// <summary>
    ///     List rejections for (libraryId, version), optionally filtered by
    ///     reason. Returns the most-prevalent rejections first (sort by
    ///     ChunkCount descending) so the LLM sees the loudest noise first.
    /// </summary>
    Task<IReadOnlyList<ExcludedSymbol>> ListAsync(string libraryId,
                                                   string version,
                                                   SymbolRejectionReason? reason,
                                                   int limit,
                                                   CancellationToken ct = default);

    /// <summary>
    ///     Insert or update each entry by Id. Existing entries with the same
    ///     Id are replaced.
    /// </summary>
    Task UpsertManyAsync(IEnumerable<ExcludedSymbol> entries, CancellationToken ct = default);

    /// <summary>
    ///     Remove rejections for (libraryId, version) whose Name matches any
    ///     entry in names (Ordinal compare). Idempotent — names not present
    ///     are silently ignored.
    /// </summary>
    Task RemoveAsync(string libraryId,
                     string version,
                     IEnumerable<string> names,
                     CancellationToken ct = default);

    /// <summary>
    ///     Wipe all rejections for (libraryId, version). Called at the start
    ///     of each rescrub so we never accumulate stale rows.
    /// </summary>
    Task DeleteAllForLibraryAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Total count of rejections for (libraryId, version). Used by the
    ///     list_excluded_symbols tool's Returned/TotalExcluded headers.
    /// </summary>
    Task<int> CountAsync(string libraryId, string version, CancellationToken ct = default);
}
```

- [ ] **Step 3.2: Build**

Run: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
Expected: succeeds, 0 errors.

- [ ] **Step 3.3: Commit**

Write `e:/tmp/msg.txt`:
```
Add IExcludedSymbolsRepository interface

Persistence contract for per-(library, version) extractor
rejections. Implementation, DI wiring, and indexes follow.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Core/Interfaces/IExcludedSymbolsRepository.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 4: Repository implementation + DI + indexes

**Files:**
- Create: `DocRAG.Database/Repositories/ExcludedSymbolsRepository.cs`
- Modify: `DocRAG.Database/DocRagDbContext.cs`
- Modify: `DocRAG.Database/Repositories/RepositoryFactory.cs`
- Modify: `DocRAG.Database/ServiceCollectionExtensions.cs`

- [ ] **Step 4.1: Create the repository**

```csharp
// ExcludedSymbolsRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB-backed implementation of IExcludedSymbolsRepository.
///     Rejections are keyed by (LibraryId, Version, Name) via a composite
///     document id.
/// </summary>
public class ExcludedSymbolsRepository : IExcludedSymbolsRepository
{
    public ExcludedSymbolsRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExcludedSymbol>> ListAsync(string libraryId,
                                                                string version,
                                                                SymbolRejectionReason? reason,
                                                                int limit,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filterBase = mContext.ExcludedSymbols
                                 .Find(e => e.LibraryId == libraryId && e.Version == version);
        var filtered = reason.HasValue
                           ? mContext.ExcludedSymbols.Find(e => e.LibraryId == libraryId
                                                             && e.Version == version
                                                             && e.Reason == reason.Value)
                           : filterBase;

        var ordered = filtered.SortByDescending(e => e.ChunkCount).Limit(limit);
        var result = await ordered.ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertManyAsync(IEnumerable<ExcludedSymbol> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.ToList();
        if (list.Count > 0)
        {
            var models = list.Select(e => new ReplaceOneModel<ExcludedSymbol>(
                                              Builders<ExcludedSymbol>.Filter.Eq(x => x.Id, e.Id),
                                              e
                                          )
                                      { IsUpsert = true });
            await mContext.ExcludedSymbols.BulkWriteAsync(models, cancellationToken: ct);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string libraryId,
                                  string version,
                                  IEnumerable<string> names,
                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(names);

        var nameList = names.ToList();
        if (nameList.Count > 0)
        {
            var filter = Builders<ExcludedSymbol>.Filter.And(
                Builders<ExcludedSymbol>.Filter.Eq(e => e.LibraryId, libraryId),
                Builders<ExcludedSymbol>.Filter.Eq(e => e.Version, version),
                Builders<ExcludedSymbol>.Filter.In(e => e.Name, nameList)
            );
            await mContext.ExcludedSymbols.DeleteManyAsync(filter, ct);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAllForLibraryAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        await mContext.ExcludedSymbols
                      .DeleteManyAsync(e => e.LibraryId == libraryId && e.Version == version, ct);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var count = await mContext.ExcludedSymbols
                                  .CountDocumentsAsync(e => e.LibraryId == libraryId && e.Version == version, cancellationToken: ct);
        var result = (int) count;
        return result;
    }
}
```

- [ ] **Step 4.2: Add the collection accessor and indexes to `DocRagDbContext.cs`**

Add a property after `Bm25Shards` (around line 56):

```csharp
    public IMongoCollection<ExcludedSymbol> ExcludedSymbols =>
        mDatabase.GetCollection<ExcludedSymbol>(CollectionExcludedSymbols);
```

Add the index creation in `EnsureIndexesAsync` (append at the end, after the Bm25Shards block):

```csharp
        // ExcludedSymbols: compound on (LibraryId, Version, Reason) for the
        // list_excluded_symbols reason filter, plus (LibraryId, Version, Name)
        // for fast remove-by-name when the LLM promotes/demotes tokens.
        var excludedKeys = Builders<ExcludedSymbol>.IndexKeys;
        await ExcludedSymbols.Indexes.CreateOneAsync(new CreateIndexModel<ExcludedSymbol>(excludedKeys.Combine(excludedKeys.Ascending(e => e.LibraryId),
                                                                                                                excludedKeys.Ascending(e => e.Version),
                                                                                                                excludedKeys.Ascending(e => e.Reason)
                                                                                                               )
                                                                                          ),
                                                     cancellationToken: ct
                                                    );
        await ExcludedSymbols.Indexes.CreateOneAsync(new CreateIndexModel<ExcludedSymbol>(excludedKeys.Combine(excludedKeys.Ascending(e => e.LibraryId),
                                                                                                                excludedKeys.Ascending(e => e.Version),
                                                                                                                excludedKeys.Ascending(e => e.Name)
                                                                                                               )
                                                                                          ),
                                                     cancellationToken: ct
                                                    );
```

Add the const at the bottom of the constants block:

Old:
```csharp
    private const string CollectionBm25Shards = "bm25Shards";
    private const string Bm25BucketName = "bm25";
```

New:
```csharp
    private const string CollectionBm25Shards = "bm25Shards";
    private const string CollectionExcludedSymbols = "library_excluded_symbols";
    private const string Bm25BucketName = "bm25";
```

- [ ] **Step 4.3: Add the `RepositoryFactory` accessor**

Append this method to `RepositoryFactory.cs` after `GetBm25ShardRepository`:

```csharp
    /// <summary>
    ///     Get an excluded-symbols repository for the specified database
    ///     profile. Stores per-(library, version) extractor rejections
    ///     captured during rescrub.
    /// </summary>
    public IExcludedSymbolsRepository GetExcludedSymbolsRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new ExcludedSymbolsRepository(context);
        return result;
    }
```

- [ ] **Step 4.4: Register in DI**

In `ServiceCollectionExtensions.cs`, after the `IBm25ShardRepository` registration (line 51):

Old:
```csharp
        services.AddSingleton<IBm25ShardRepository, Bm25ShardRepository>();

        return services;
```

New:
```csharp
        services.AddSingleton<IBm25ShardRepository, Bm25ShardRepository>();
        services.AddSingleton<IExcludedSymbolsRepository, ExcludedSymbolsRepository>();

        return services;
```

- [ ] **Step 4.5: Build to verify everything wires up**

Run: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
Expected: succeeds, 0 errors.

- [ ] **Step 4.6: Commit**

Write `e:/tmp/msg.txt`:
```
Add ExcludedSymbolsRepository with composite indexes

Mongo collection library_excluded_symbols stores per-(library,
version) rejections produced by SymbolExtractor. Indexes cover
the two query shapes the new MCP tools need: filter-by-reason
(list_excluded_symbols) and remove-by-name (promotion/demotion).
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Database/Repositories/ExcludedSymbolsRepository.cs DocRAG.Database/DocRagDbContext.cs DocRAG.Database/Repositories/RepositoryFactory.cs DocRAG.Database/ServiceCollectionExtensions.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 5: `Stoplist.Match` overload (profile-aware)

**Files:**
- Modify: `DocRAG.Ingestion/Symbols/Stoplist.cs`
- Create: `DocRAG.Tests/Symbols/StoplistTests.cs`

- [ ] **Step 5.1: Write failing tests**

Create `DocRAG.Tests/Symbols/StoplistTests.cs`:

```csharp
// StoplistTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Symbols;

public sealed class StoplistTests
{
    [Fact]
    public void MatchReturnsGlobalForUniversalStoplistHit()
    {
        var profile = MakeProfile([]);

        var match = Stoplist.Match("the", profile);

        Assert.Equal(StoplistMatch.Global, match);
    }

    [Fact]
    public void MatchReturnsLibraryForProfileStoplistHit()
    {
        var profile = MakeProfile(["along"]);

        var match = Stoplist.Match("along", profile);

        Assert.Equal(StoplistMatch.Library, match);
    }

    [Fact]
    public void MatchReturnsNoneForNonStoplistedToken()
    {
        var profile = MakeProfile([]);

        var match = Stoplist.Match("MoveLinear", profile);

        Assert.Equal(StoplistMatch.None, match);
    }

    [Fact]
    public void MatchIsCaseInsensitiveOnLibraryStoplist()
    {
        var profile = MakeProfile(["Along"]);

        var match = Stoplist.Match("along", profile);

        Assert.Equal(StoplistMatch.Library, match);
    }

    [Fact]
    public void MatchPrefersGlobalOverLibrary()
    {
        // "the" is in the global stoplist; if also added to per-library
        // stoplist, the response surfaces the global hit (more specific
        // diagnostic for the LLM — they didn't add 'the' themselves).
        var profile = MakeProfile(["the"]);

        var match = Stoplist.Match("the", profile);

        Assert.Equal(StoplistMatch.Global, match);
    }

    [Fact]
    public void ContainsOverloadStillWorksWithoutProfile()
    {
        Assert.True(Stoplist.Contains("the"));
        Assert.False(Stoplist.Contains("MoveLinear"));
    }

    private static LibraryProfile MakeProfile(IReadOnlyList<string> stoplist)
    {
        var result = new LibraryProfile
                         {
                             Id = "test-lib/1.0",
                             LibraryId = "test-lib",
                             Version = "1.0",
                             Source = "test",
                             Stoplist = stoplist
                         };
        return result;
    }
}
```

- [ ] **Step 5.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~StoplistTests" -v minimal`
Expected: FAIL — `StoplistMatch` doesn't exist; `Match` doesn't exist.

- [ ] **Step 5.3: Add `StoplistMatch` enum and `Match` overload**

Replace the contents of `DocRAG.Ingestion/Symbols/Stoplist.cs` (the existing file). Keep the existing `Contains(candidate)` overload and the `smStopwords` set unchanged. Add the enum at the top of the namespace and the new `Match` method:

Old (around line 5):
```csharp
namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Backup filter for the symbol extractor. Words matching the stoplist
```

New:
```csharp
namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Result of a profile-aware stoplist check.
/// </summary>
public enum StoplistMatch
{
    /// <summary>Token was not in any stoplist.</summary>
    None,

    /// <summary>Token matched the universal Stoplist.</summary>
    Global,

    /// <summary>Token matched LibraryProfile.Stoplist.</summary>
    Library
}

/// <summary>
///     Backup filter for the symbol extractor. Words matching the stoplist
```

Add a `using` for `LibraryProfile`:

Old (top of file):
```csharp
namespace DocRAG.Ingestion.Symbols;
```

New:
```csharp
#region Usings

using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Symbols;
```

Add the `Match` method inside the `Stoplist` class, right after the existing `Contains` method:

```csharp
    /// <summary>
    ///     Profile-aware stoplist check. Returns Global if the candidate is
    ///     in the universal stoplist, Library if it's in the profile's
    ///     stoplist (case-insensitive), else None. Global wins when both
    ///     match — surfaces the more specific diagnostic.
    /// </summary>
    public static StoplistMatch Match(string candidate, LibraryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);

        var inGlobal = Contains(candidate);
        var inLibrary = profile.Stoplist.Contains(candidate, StringComparer.OrdinalIgnoreCase);

        var result = (inGlobal, inLibrary) switch
        {
            (true, _) => StoplistMatch.Global,
            (false, true) => StoplistMatch.Library,
            _ => StoplistMatch.None
        };
        return result;
    }
```

- [ ] **Step 5.4: Run tests to verify they pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~StoplistTests" -v minimal`
Expected: PASS (all six tests).

- [ ] **Step 5.5: Commit**

Write `e:/tmp/msg.txt`:
```
Add profile-aware Stoplist.Match overload

The new Match returns a StoplistMatch enum (None/Global/Library)
so the symbol extractor can label rejection reasons accurately
once it consults LibraryProfile.Stoplist. The original Contains
overload is unchanged so existing callers keep working.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Symbols/Stoplist.cs DocRAG.Tests/Symbols/StoplistTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 6: `RejectedToken` and `ExtractedSymbols.Rejected`

**Files:**
- Create: `DocRAG.Ingestion/Symbols/RejectedToken.cs`
- Modify: `DocRAG.Ingestion/Symbols/ExtractedSymbols.cs`

- [ ] **Step 6.1: Create `RejectedToken`**

```csharp
// RejectedToken.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     A single token the extractor rejected. The rescrub pass aggregates
///     these per (library, version) into ExcludedSymbol records — the
///     extractor itself does not capture sample sentences (it only sees a
///     single chunk's content; sampling needs the full corpus).
/// </summary>
public record RejectedToken
{
    /// <summary>
    ///     Exact token text (case preserved).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Why the extractor rejected this token.
    /// </summary>
    public required SymbolRejectionReason Reason { get; init; }
}
```

- [ ] **Step 6.2: Modify `ExtractedSymbols`**

Add the `Rejected` property after `PrimaryQualifiedName`:

Old:
```csharp
public record ExtractedSymbols
{
    /// <summary>
    ///     All symbols extracted from the chunk content.
    /// </summary>
    public required IReadOnlyList<Symbol> Symbols { get; init; }

    /// <summary>
    ///     The most prominent symbol's Name. Null when no symbols survived
    ///     the keep rules.
    /// </summary>
    public string? PrimaryQualifiedName { get; init; }

    /// <summary>
    ///     Empty result. Used when content has no surviving candidates.
    /// </summary>
    public static ExtractedSymbols Empty { get; } = new()
                                                        {
                                                            Symbols = Array.Empty<Symbol>()
                                                        };
}
```

New:
```csharp
public record ExtractedSymbols
{
    /// <summary>
    ///     All symbols extracted from the chunk content.
    /// </summary>
    public required IReadOnlyList<Symbol> Symbols { get; init; }

    /// <summary>
    ///     The most prominent symbol's Name. Null when no symbols survived
    ///     the keep rules.
    /// </summary>
    public string? PrimaryQualifiedName { get; init; }

    /// <summary>
    ///     Tokens the extractor rejected, with their reason. Default empty
    ///     so callers that don't care about rejections (production read
    ///     paths, tests pre-dating rejection capture) need no changes.
    /// </summary>
    public IReadOnlyList<RejectedToken> Rejected { get; init; } = [];

    /// <summary>
    ///     Empty result. Used when content has no surviving candidates.
    /// </summary>
    public static ExtractedSymbols Empty { get; } = new()
                                                        {
                                                            Symbols = Array.Empty<Symbol>()
                                                        };
}
```

- [ ] **Step 6.3: Build**

Run: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
Expected: succeeds, 0 errors. Existing tests unchanged because the new field has a default value.

- [ ] **Step 6.4: Commit**

Write `e:/tmp/msg.txt`:
```
Add RejectedToken and ExtractedSymbols.Rejected

Carries per-token rejection reason out of the extractor for the
rescrub aggregator to consume. Default empty list keeps existing
extract callers source-compatible.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Symbols/RejectedToken.cs DocRAG.Ingestion/Symbols/ExtractedSymbols.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 7: Rewrite `SymbolExtractor.IsAdmissible` to return reasons

This is the largest task. The behavior of which tokens survive must NOT change — only the labeling of the why.

**Files:**
- Modify: `DocRAG.Ingestion/Symbols/SymbolExtractor.cs`
- Modify: `DocRAG.Tests/Symbols/SymbolExtractorTests.cs`

- [ ] **Step 7.1: Write failing tests**

Append to `DocRAG.Tests/Symbols/SymbolExtractorTests.cs` (before the closing `}` of the class):

```csharp
[Fact]
public void RejectionReasonGlobalStoplist()
{
    var profile = MakeProfile([]);
    var extractor = new SymbolExtractor();

    var result = extractor.Extract("The axis homes to the marker.", profile);

    Assert.Contains(result.Rejected, r => r.Name == "The" && r.Reason == SymbolRejectionReason.GlobalStoplist);
}

[Fact]
public void RejectionReasonLibraryStoplist()
{
    var profile = MakeProfileWithStoplist(["BrandX"]);
    var extractor = new SymbolExtractor();

    var result = extractor.Extract("Use BrandX hardware to drive the axis.", profile);

    Assert.Contains(result.Rejected, r => r.Name == "BrandX" && r.Reason == SymbolRejectionReason.LibraryStoplist);
    Assert.DoesNotContain(result.Symbols, s => s.Name == "BrandX");
}

[Fact]
public void RejectionReasonUnit()
{
    var profile = MakeProfile([]);
    var extractor = new SymbolExtractor();

    var result = extractor.Extract("The signal is 100 GHz at peak.", profile);

    Assert.Contains(result.Rejected, r => r.Name == "GHz" && r.Reason == SymbolRejectionReason.Unit);
}

[Fact]
public void RejectionReasonBelowMinLength()
{
    var profile = MakeProfile([]);
    var extractor = new SymbolExtractor();

    var result = extractor.Extract("class X { }", profile);

    Assert.Contains(result.Rejected, r => r.Name == "X" && r.Reason == SymbolRejectionReason.BelowMinLength);
}

[Fact]
public void RejectionReasonLikelyAbbreviation()
{
    // RAM has prose mentions ≥ threshold but is short all-uppercase, so
    // IsLikelyAbbreviation blocks the prose-frequent rule. No other keep
    // signal applies — the reason should be LikelyAbbreviation, NOT
    // NoStructureSignal.
    var profile = MakeProfile([]);
    var extractor = new SymbolExtractor(proseMentionThreshold: 3);
    var corpus = new CorpusContext { ProseMentionCounts = new Dictionary<string, int> { ["RAM"] = 5 } };

    var result = extractor.Extract("The RAM stores the data.", profile, corpus);

    Assert.Contains(result.Rejected, r => r.Name == "RAM" && r.Reason == SymbolRejectionReason.LikelyAbbreviation);
}

[Fact]
public void RejectionReasonNoStructureSignal()
{
    // "alongthing" is not in stoplist, not a unit, length OK, but has no
    // mid-word capital, no underscore, no callable shape, no container,
    // no prose-frequent mentions, not declared. NoStructureSignal.
    var profile = MakeProfile([]);
    var extractor = new SymbolExtractor();

    var result = extractor.Extract("alongthing.", profile);

    Assert.Contains(result.Rejected, r => r.Name == "alongthing" && r.Reason == SymbolRejectionReason.NoStructureSignal);
}

[Fact]
public void LibraryStoplistOverridesLikelySymbols()
{
    // If a token is in BOTH lists, stoplist wins (matches existing
    // extraction behavior — Stoplist is a hard reject).
    var profile = new LibraryProfile
                      {
                          Id = "test-lib/1.0",
                          LibraryId = "test-lib",
                          Version = "1.0",
                          Source = "test",
                          LikelySymbols = ["Foo"],
                          Stoplist = ["Foo"]
                      };
    var extractor = new SymbolExtractor();

    var result = extractor.Extract("Configure the Foo widget.", profile);

    Assert.DoesNotContain(result.Symbols, s => s.Name == "Foo");
    Assert.Contains(result.Rejected, r => r.Name == "Foo" && r.Reason == SymbolRejectionReason.LibraryStoplist);
}

private static LibraryProfile MakeProfileWithStoplist(IReadOnlyList<string> stoplist)
{
    var result = new LibraryProfile
                     {
                         Id = "test-lib/1.0",
                         LibraryId = "test-lib",
                         Version = "1.0",
                         Source = "test",
                         Stoplist = stoplist
                     };
    return result;
}
```

- [ ] **Step 7.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~SymbolExtractorTests" -v minimal`
Expected: 7 new tests fail (Rejected list is empty in current implementation).

- [ ] **Step 7.3: Rewrite `SymbolExtractor.cs`**

The replacement preserves all existing behavior — every kept token stays kept — but labels the rejected ones. Key restructuring:

- `IsAdmissible(token, likelySet, corpus)` → `GetRejectionReason(token, profile, likelySet, corpus)` returns `SymbolRejectionReason?` (null when admissible).
- New private enum `ProseFrequentResult { Frequent, NotFrequent, BlockedByAbbreviation }` so `LikelyAbbreviation` can be detected without changing the keep set.
- `Extract()` collects rejections in parallel.

Replace lines 50–128 of `SymbolExtractor.cs` (everything from `/// <summary>` above `Extract` through the end of `IsProseFrequent`) with:

```csharp
    /// <summary>
    ///     Extract symbols from a single chunk's content. Surviving symbols
    ///     are returned in <see cref="ExtractedSymbols.Symbols"/>; tokens
    ///     that failed admissibility are returned in <see cref="ExtractedSymbols.Rejected"/>
    ///     with the specific reason so a corpus-level aggregator can record
    ///     them for later review.
    /// </summary>
    public ExtractedSymbols Extract(string content,
                                    LibraryProfile profile,
                                    CorpusContext? corpusContext = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(profile);

        var corpus = corpusContext ?? CorpusContext.Empty;
        var tokens = IdentifierTokenizer.Tokenize(content);
        var likelySet = BuildLikelySet(profile);

        var kept = new List<Symbol>();
        var rejected = new List<RejectedToken>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenRejected = new HashSet<string>(StringComparer.Ordinal);

        foreach(var token in tokens)
        {
            var reason = GetRejectionReason(token, profile, likelySet, corpus);
            if (reason.HasValue)
            {
                if (!seenRejected.Contains(token.Name))
                {
                    seenRejected.Add(token.Name);
                    rejected.Add(new RejectedToken { Name = token.Name, Reason = reason.Value });
                }
            }
            else
            {
                var symbol = Classify(token, likelySet, profile);
                if (!seenNames.Contains(symbol.Name))
                {
                    seenNames.Add(symbol.Name);
                    kept.Add(symbol);
                }
            }
        }

        var primary = PickPrimaryName(kept);
        var result = new ExtractedSymbols
                         {
                             Symbols = kept,
                             PrimaryQualifiedName = primary,
                             Rejected = rejected
                         };
        return result;
    }

    /// <summary>
    ///     Return the rejection reason for this token, or null if it is
    ///     admissible. Resolution order matches the original IsAdmissible:
    ///     stoplist → unit → min-length → keep-rule failure (with
    ///     LikelyAbbreviation as a sub-reason of the prose-frequent path
    ///     when applicable).
    /// </summary>
    private SymbolRejectionReason? GetRejectionReason(TokenCandidate token,
                                                      LibraryProfile profile,
                                                      HashSet<string> likelySet,
                                                      CorpusContext corpus)
    {
        var leafMatch = Stoplist.Match(token.LeafName, profile);
        var nameMatch = leafMatch == StoplistMatch.None ? Stoplist.Match(token.Name, profile) : leafMatch;

        var unitHit = UnitsLookup.IsUnit(token.LeafName) || UnitsLookup.IsUnit(token.Name);
        var belowMin = token.Name.Length < MinIdentifierLength;

        SymbolRejectionReason? result = (nameMatch, unitHit, belowMin) switch
        {
            (StoplistMatch.Global, _, _) => SymbolRejectionReason.GlobalStoplist,
            (StoplistMatch.Library, _, _) => SymbolRejectionReason.LibraryStoplist,
            (StoplistMatch.None, true, _) => SymbolRejectionReason.Unit,
            (StoplistMatch.None, false, true) => SymbolRejectionReason.BelowMinLength,
            _ => ResolveKeepRuleReason(token, likelySet, corpus)
        };
        return result;
    }

    private SymbolRejectionReason? ResolveKeepRuleReason(TokenCandidate token,
                                                          HashSet<string> likelySet,
                                                          CorpusContext corpus)
    {
        var name = token.Name;
        var leaf = token.LeafName;

        bool inLikely = likelySet.Contains(name) || likelySet.Contains(leaf);
        bool inCodeFence = corpus.CodeFenceSymbols.Contains(name) || corpus.CodeFenceSymbols.Contains(leaf);
        bool hasContainer = !string.IsNullOrEmpty(token.Container);
        bool hasInternalStructure = HasInternalStructure(name);
        var proseState = ResolveProseFrequent(name, leaf, corpus);

        bool admissible = token.IsDeclared
                       || inLikely
                       || inCodeFence
                       || hasContainer
                       || hasInternalStructure
                       || token.HasCallableShape
                       || token.HasGenericShape
                       || proseState == ProseFrequentResult.Frequent;

        SymbolRejectionReason? result;
        if (admissible)
            result = null;
        else
            result = proseState == ProseFrequentResult.BlockedByAbbreviation
                         ? SymbolRejectionReason.LikelyAbbreviation
                         : SymbolRejectionReason.NoStructureSignal;
        return result;
    }

    private ProseFrequentResult ResolveProseFrequent(string name, string leaf, CorpusContext corpus)
    {
        var nameState = ProseFrequentState(name, corpus);
        var leafState = ProseFrequentState(leaf, corpus);

        var combined = (nameState, leafState) switch
        {
            (ProseFrequentResult.Frequent, _) => ProseFrequentResult.Frequent,
            (_, ProseFrequentResult.Frequent) => ProseFrequentResult.Frequent,
            (ProseFrequentResult.BlockedByAbbreviation, _) => ProseFrequentResult.BlockedByAbbreviation,
            (_, ProseFrequentResult.BlockedByAbbreviation) => ProseFrequentResult.BlockedByAbbreviation,
            _ => ProseFrequentResult.NotFrequent
        };
        return combined;
    }

    private ProseFrequentResult ProseFrequentState(string identifier, CorpusContext corpus)
    {
        var hasMentions = corpus.ProseMentionCounts.TryGetValue(identifier, out var count);
        var aboveThreshold = hasMentions && count >= mProseMentionThreshold;
        var abbreviation = IsLikelyAbbreviation(identifier);

        var result = (aboveThreshold, abbreviation) switch
        {
            (true, false) => ProseFrequentResult.Frequent,
            (true, true) => ProseFrequentResult.BlockedByAbbreviation,
            _ => ProseFrequentResult.NotFrequent
        };
        return result;
    }

    private enum ProseFrequentResult
    {
        NotFrequent,
        Frequent,
        BlockedByAbbreviation
    }
```

Add a `using` for `DocRAG.Core.Enums` at the top if it's not already present (it should be — the file already declares `using DocRAG.Core.Enums;` at line 7).

Remove the obsolete `IsAdmissible`, `ShouldKeep`, and `IsProseFrequent` methods (the new structure replaces them all). The old bodies, for reference, were lines 86–128 in the original file.

- [ ] **Step 7.4: Run extractor tests to verify they pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~SymbolExtractorTests" -v minimal`
Expected: PASS. All existing tests + 7 new ones.

- [ ] **Step 7.5: Run the full test suite to confirm no regressions**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj -v minimal`
Expected: PASS.

- [ ] **Step 7.6: Commit**

Write `e:/tmp/msg.txt`:
```
Return rejection reasons from SymbolExtractor

IsAdmissible is replaced by GetRejectionReason, which preserves
the original keep/reject set verbatim while attaching a labelled
reason to every reject. The prose-frequent rule now distinguishes
"blocked by abbreviation guard" from "not frequent enough", so
short all-caps tokens like RAM get the more diagnostic
LikelyAbbreviation reason instead of the catch-all
NoStructureSignal.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Symbols/SymbolExtractor.cs DocRAG.Tests/Symbols/SymbolExtractorTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 8: `SampleWindowExtractor`

**Files:**
- Create: `DocRAG.Ingestion/Symbols/SampleWindowExtractor.cs`
- Create: `DocRAG.Tests/Symbols/SampleWindowExtractorTests.cs`

- [ ] **Step 8.1: Write failing tests**

```csharp
// SampleWindowExtractorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Symbols;

public sealed class SampleWindowExtractorTests
{
    [Fact]
    public void ExtractsWindowAroundFirstOccurrence()
    {
        var content = "The MoveLinear command moves the axis to the target position.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.Contains("MoveLinear", sample);
    }

    [Fact]
    public void CapsTotalLengthAt200Characters()
    {
        // Long content with token in the middle. The window before+token+
        // after must not exceed 200 chars total.
        var prefix = new string('a', 500);
        var suffix = new string('b', 500);
        var content = $"{prefix} TOKEN {suffix}";

        var sample = SampleWindowExtractor.Extract(content, "TOKEN");

        Assert.NotNull(sample);
        Assert.True(sample.Length <= 200, $"sample length was {sample.Length}, expected <= 200");
        Assert.Contains("TOKEN", sample);
    }

    [Fact]
    public void TrimsToWordBoundaries()
    {
        // Boundary between window edge and the next word should fall on
        // whitespace, not in the middle of a word.
        var content = "averylongprefixwordbeforethetoken Symbol thenanotherverylongsuffixword";

        var sample = SampleWindowExtractor.Extract(content, "Symbol");

        Assert.NotNull(sample);
        // Should not start mid-word: first non-whitespace token should be
        // a complete word from the original content.
        Assert.DoesNotContain("averylongprefixwordbefore", sample);
    }

    [Fact]
    public void TokenAtStartOfChunkHasNoLeftContext()
    {
        var content = "MoveLinear is the entrypoint for axis motion.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.StartsWith("MoveLinear", sample);
    }

    [Fact]
    public void TokenAtEndOfChunkHasNoRightContext()
    {
        var content = "The entrypoint for axis motion is MoveLinear";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.EndsWith("MoveLinear", sample);
    }

    [Fact]
    public void ReturnsFirstOccurrenceWhenTokenAppearsMultipleTimes()
    {
        var content = "First MoveLinear, then later MoveLinear, and again MoveLinear.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.StartsWith("First MoveLinear", sample);
    }

    [Fact]
    public void ReturnsNullWhenTokenIsNotPresent()
    {
        var content = "The axis homes to the marker.";

        var sample = SampleWindowExtractor.Extract(content, "MissingToken");

        Assert.Null(sample);
    }

    [Fact]
    public void CollapsesInternalWhitespaceToSingleSpaces()
    {
        var content = "Use   MoveLinear\n\nto\tmove   the    axis.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.DoesNotContain("\n", sample);
        Assert.DoesNotContain("\t", sample);
        Assert.DoesNotContain("  ", sample);
    }
}
```

- [ ] **Step 8.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~SampleWindowExtractorTests" -v minimal`
Expected: FAIL — `SampleWindowExtractor` doesn't exist.

- [ ] **Step 8.3: Implement `SampleWindowExtractor`**

```csharp
// SampleWindowExtractor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Pulls a short corpus snippet around the first occurrence of a
///     token in chunk content. Used by the rejection accumulator to
///     attach 2-3 sample sentences to each ExcludedSymbol record so the
///     calling LLM can decide whether the rejection was correct.
///
///     The output is whitespace-normalized (newlines/tabs become single
///     spaces, multiple spaces collapse) and capped at WindowMaxChars
///     total characters. Returns null when the token is not present in
///     the content (defensive — should not happen if the caller's chunk
///     index is consistent, but we don't crash a rescrub over it).
/// </summary>
public static class SampleWindowExtractor
{
    /// <summary>
    ///     Extract a window around the first occurrence of <paramref name="token"/>
    ///     in <paramref name="content"/>. Token comparison is Ordinal
    ///     (case-sensitive) and uses the exact token text.
    /// </summary>
    public static string? Extract(string content, string token)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var index = content.IndexOf(token, StringComparison.Ordinal);
        string? result = null;
        if (index >= 0)
            result = BuildWindow(content, token, index);
        return result;
    }

    private static string BuildWindow(string content, string token, int index)
    {
        var leftStart = ExpandToWordBoundary(content, Math.Max(0, index - WindowSideChars), expandLeft: true);
        var rightEnd = ExpandToWordBoundary(content, Math.Min(content.Length, index + token.Length + WindowSideChars), expandLeft: false);

        var raw = content.Substring(leftStart, rightEnd - leftStart);
        var collapsed = smWhitespaceRegex.Replace(raw, " ").Trim();

        var capped = collapsed.Length <= WindowMaxChars
                         ? collapsed
                         : TruncateAroundToken(collapsed, token);
        return capped;
    }

    /// <summary>
    ///     Walk left or right to the nearest whitespace so the window edge
    ///     sits on a word boundary instead of mid-word. Idempotent at the
    ///     content edges.
    /// </summary>
    private static int ExpandToWordBoundary(string content, int position, bool expandLeft)
    {
        var result = position;
        if (expandLeft)
        {
            while (result > 0 && !char.IsWhiteSpace(content[result - 1]))
                result--;
        }
        else
        {
            while (result < content.Length && !char.IsWhiteSpace(content[result]))
                result++;
        }
        return result;
    }

    /// <summary>
    ///     If the trimmed window still exceeds the cap, truncate from the
    ///     longer side while keeping the token visible.
    /// </summary>
    private static string TruncateAroundToken(string text, string token)
    {
        var tokenIndex = text.IndexOf(token, StringComparison.Ordinal);
        var beforeLen = tokenIndex;
        var afterLen = text.Length - tokenIndex - token.Length;
        var available = WindowMaxChars - token.Length;
        var halfAvailable = available / 2;

        var keepBefore = Math.Min(beforeLen, halfAvailable);
        var keepAfter = Math.Min(afterLen, available - keepBefore);
        keepBefore = Math.Min(beforeLen, available - keepAfter);

        var startIdx = tokenIndex - keepBefore;
        var endIdx = tokenIndex + token.Length + keepAfter;
        var result = text.Substring(startIdx, endIdx - startIdx).Trim();
        return result;
    }

    private static readonly Regex smWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private const int WindowSideChars = 100;
    private const int WindowMaxChars = 200;
}
```

- [ ] **Step 8.4: Run tests to verify pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~SampleWindowExtractorTests" -v minimal`
Expected: PASS. All 8 tests.

- [ ] **Step 8.5: Commit**

Write `e:/tmp/msg.txt`:
```
Add SampleWindowExtractor for rejection sample sentences

Pulls a 200-char window around the first occurrence of a token
in chunk content. Whitespace-normalized, word-boundary trimmed,
returns null defensively when the token is not present. The
rejection accumulator (next commit) will use this to attach 2-3
sample sentences to each ExcludedSymbol record.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Symbols/SampleWindowExtractor.cs DocRAG.Tests/Symbols/SampleWindowExtractorTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 9: `RejectionAccumulator`

**Files:**
- Create: `DocRAG.Ingestion/Recon/RejectionAccumulator.cs`
- Create: `DocRAG.Tests/Recon/RejectionAccumulatorTests.cs`

- [ ] **Step 9.1: Write failing tests**

```csharp
// RejectionAccumulatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Ingestion.Recon;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Recon;

public sealed class RejectionAccumulatorTests
{
    [Fact]
    public void AggregatesChunkCountAcrossChunks()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 3);

        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "first along sentence here");
        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 1,
                   chunkContent: "second along sentence here");
        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 2,
                   chunkContent: "third along sentence here");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "along");
        Assert.Equal(3, entry.ChunkCount);
    }

    [Fact]
    public void CapturesUpToThreeSamplesAcrossThirds()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 30);

        // Token in chunk 0 (first third), 15 (middle third), 29 (last third).
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "first noise occurrence");
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 15,
                   chunkContent: "middle noise occurrence");
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 29,
                   chunkContent: "last noise occurrence");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "noise");
        Assert.Equal(3, entry.SampleSentences.Count);
        Assert.Contains(entry.SampleSentences, s => s.Contains("first"));
        Assert.Contains(entry.SampleSentences, s => s.Contains("middle"));
        Assert.Contains(entry.SampleSentences, s => s.Contains("last"));
    }

    [Fact]
    public void OnlySamplesOncePerThird()
    {
        // Six occurrences in the first third — only the first should
        // produce a sample.
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 30);

        for (int i = 0; i < 6; i++)
        {
            acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                       chunkIndex: i,
                       chunkContent: $"chunk {i} content with noise inside");
        }

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "noise");
        Assert.Single(entry.SampleSentences);
        Assert.Contains("chunk 0", entry.SampleSentences[0]);
    }

    [Fact]
    public void TwoOccurrencesProduceTwoSamples()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 10);

        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "alpha noise here");
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 9,
                   chunkContent: "omega noise here");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "noise");
        Assert.Equal(2, entry.SampleSentences.Count);
    }

    [Fact]
    public void FirstSeenReasonWinsOnConflictingReports()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 2);

        acc.Record(new RejectedToken { Name = "tok", Reason = SymbolRejectionReason.LibraryStoplist },
                   chunkIndex: 0,
                   chunkContent: "first tok use");
        // Theoretically impossible — same token, same code path — but
        // defensive: record a second reason and confirm the first wins.
        acc.Record(new RejectedToken { Name = "tok", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 1,
                   chunkContent: "second tok use");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "tok");
        Assert.Equal(SymbolRejectionReason.LibraryStoplist, entry.Reason);
    }

    [Fact]
    public void IdAndCapturedUtcArePopulated()
    {
        var acc = new RejectionAccumulator("aerotech-aeroscript", "1.0", totalChunks: 1);

        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "axis moves along the path.");

        var built = acc.Build();
        var entry = Assert.Single(built);
        Assert.Equal("aerotech-aeroscript/1.0/along", entry.Id);
        Assert.True((DateTime.UtcNow - entry.CapturedUtc).TotalSeconds < 5);
    }

    [Fact]
    public void DropsNullSamplesFromMissingTokens()
    {
        // Defensive path — chunkContent doesn't contain the token (should
        // not happen in production, but accumulator must not crash). The
        // entry is still recorded but with no samples for that chunk.
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 1);

        acc.Record(new RejectedToken { Name = "missing", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "this content does not contain the token");

        var built = acc.Build();
        var entry = Assert.Single(built);
        Assert.Equal(1, entry.ChunkCount);
        Assert.Empty(entry.SampleSentences);
    }
}
```

- [ ] **Step 9.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~RejectionAccumulatorTests" -v minimal`
Expected: FAIL — `RejectionAccumulator` doesn't exist.

- [ ] **Step 9.3: Implement `RejectionAccumulator`**

```csharp
// RejectionAccumulator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Ingestion.Recon;

/// <summary>
///     Aggregates per-chunk RejectedToken reports across a rescrub pass
///     into a list of ExcludedSymbol entries ready to upsert. For each
///     token name:
///       — Records the first reason seen (extractor reasons are
///         deterministic from the token shape, so conflicts are rare).
///       — Increments ChunkCount on every report.
///       — Captures up to three sample snippets, one per third of the
///         chunk stream (so spread-out noise produces spread-out samples).
///
///     Use by RescrubService: construct once with the total chunk count,
///     call Record per (chunk, rejectedToken), then Build at the end.
/// </summary>
public sealed class RejectionAccumulator
{
    public RejectionAccumulator(string libraryId, string version, int totalChunks)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (totalChunks < 1)
            throw new ArgumentOutOfRangeException(nameof(totalChunks), totalChunks, "totalChunks must be >= 1");

        mLibraryId = libraryId;
        mVersion = version;
        mTotalChunks = totalChunks;
    }

    private readonly string mLibraryId;
    private readonly string mVersion;
    private readonly int mTotalChunks;
    private readonly Dictionary<string, AccumulatorEntry> mEntries = new(StringComparer.Ordinal);

    /// <summary>
    ///     Record one rejection observation. <paramref name="chunkIndex"/>
    ///     is the position of the chunk in the rescrub iteration order;
    ///     used to bucket the sample by corpus third.
    /// </summary>
    public void Record(RejectedToken token, int chunkIndex, string chunkContent)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(chunkContent);

        var entry = mEntries.TryGetValue(token.Name, out var existing)
                        ? existing
                        : NewEntry(token);
        entry.ChunkCount++;
        TryCaptureSample(entry, chunkIndex, chunkContent, token.Name);
        mEntries[token.Name] = entry;
    }

    /// <summary>
    ///     Materialize the accumulator state into ExcludedSymbol records
    ///     ready for IExcludedSymbolsRepository.UpsertManyAsync.
    /// </summary>
    public IReadOnlyList<ExcludedSymbol> Build()
    {
        var nowUtc = DateTime.UtcNow;
        var result = mEntries.Values.Select(entry => new ExcludedSymbol
                                                         {
                                                             Id = ExcludedSymbol.MakeId(mLibraryId, mVersion, entry.Name),
                                                             LibraryId = mLibraryId,
                                                             Version = mVersion,
                                                             Name = entry.Name,
                                                             Reason = entry.Reason,
                                                             SampleSentences = entry.Samples
                                                                                    .Where(s => s != null)
                                                                                    .Select(s => s!)
                                                                                    .ToList(),
                                                             ChunkCount = entry.ChunkCount,
                                                             CapturedUtc = nowUtc
                                                         })
                              .ToList();
        return result;
    }

    private AccumulatorEntry NewEntry(RejectedToken token) => new()
                                                                  {
                                                                      Name = token.Name,
                                                                      Reason = token.Reason,
                                                                      Samples = new string?[ThirdsBuckets],
                                                                      ChunkCount = 0
                                                                  };

    private void TryCaptureSample(AccumulatorEntry entry, int chunkIndex, string chunkContent, string tokenName)
    {
        var bucket = ResolveBucket(chunkIndex);
        var alreadyHaveSample = entry.Samples[bucket] != null;
        if (!alreadyHaveSample)
        {
            var sample = SampleWindowExtractor.Extract(chunkContent, tokenName);
            if (sample != null)
                entry.Samples[bucket] = sample;
        }
    }

    private int ResolveBucket(int chunkIndex)
    {
        var third = mTotalChunks / ThirdsBuckets;
        var safeThird = Math.Max(third, 1);
        var bucket = Math.Min(chunkIndex / safeThird, ThirdsBuckets - 1);
        return bucket;
    }

    private sealed class AccumulatorEntry
    {
        public required string Name { get; init; }
        public required SymbolRejectionReason Reason { get; init; }
        public required string?[] Samples { get; init; }
        public int ChunkCount { get; set; }
    }

    private const int ThirdsBuckets = 3;
}
```

- [ ] **Step 9.4: Run tests to verify pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~RejectionAccumulatorTests" -v minimal`
Expected: PASS. All 7 tests.

- [ ] **Step 9.5: Commit**

Write `e:/tmp/msg.txt`:
```
Add RejectionAccumulator for rescrub-time aggregation

For each token rejected by the extractor, the accumulator
records the reason (first-seen wins), increments a chunk count,
and captures up to three sample snippets bucketed by corpus
third so widely-distributed noise produces widely-distributed
samples. Build() materializes ExcludedSymbol records ready to
upsert.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Recon/RejectionAccumulator.cs DocRAG.Tests/Recon/RejectionAccumulatorTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 10: Add `ExcludedCount` and `Hints` to `RescrubResult`

**Files:**
- Modify: `DocRAG.Core/Models/RescrubResult.cs`

- [ ] **Step 10.1: Add the fields**

After the `ReconNeeded` property, add:

```csharp
    /// <summary>
    ///     Total number of distinct tokens the extractor rejected during
    ///     this rescrub. Zero when the rescrub was a no-op (ReconNeeded or
    ///     no chunks).
    /// </summary>
    public int ExcludedCount { get; init; }

    /// <summary>
    ///     Optional advisory text the calling LLM may surface to the user.
    ///     Populated only when the rescrub crossed both threshold values
    ///     (≥5% of candidates excluded AND ≥20 absolute exclusions);
    ///     otherwise empty.
    /// </summary>
    public IReadOnlyList<string> Hints { get; init; } = [];
```

- [ ] **Step 10.2: Build**

Run: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
Expected: succeeds. Existing `RescrubServiceTests` continue to compile because the new fields default to zero / empty.

- [ ] **Step 10.3: Commit**

Write `e:/tmp/msg.txt`:
```
Add ExcludedCount and Hints to RescrubResult

Defaults preserve existing test/serializer behavior. Population
follows in the next commit when RescrubService is wired up to
the rejection accumulator and the threshold rule.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Core/Models/RescrubResult.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 11: Wire `RescrubService` to capture and persist rejections + emit hints

**Files:**
- Modify: `DocRAG.Ingestion/Recon/RescrubService.cs`
- Modify: `DocRAG.Mcp/Tools/RescrubTools.cs`
- Modify: `DocRAG.Tests/Recon/RescrubServiceTests.cs`

- [ ] **Step 11.1: Write failing tests**

Append to `DocRAG.Tests/Recon/RescrubServiceTests.cs` (before the closing `}` of the class). The tests use the same `MakeService` / `MakeProfile` / `MakeLegacyChunk` helpers already in the file.

```csharp
[Fact]
public async Task PersistsRejectionsToExcludedRepository()
{
    var service = MakeService();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

    profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
    chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
             .Returns(new[] { MakeLegacyChunk("The axis homes when MoveLinear runs.") });

    var result = await service.RescrubAsync(chunkRepo,
                                            profileRepo,
                                            indexRepo,
                                            bm25ShardRepo,
                                            excludedRepo,
                                            "lib",
                                            "1.0",
                                            new RescrubOptions(),
                                            TestContext.Current.CancellationToken
                                           );

    await excludedRepo.Received(1).DeleteAllForLibraryAsync("lib", "1.0", Arg.Any<CancellationToken>());
    await excludedRepo.Received(1).UpsertManyAsync(Arg.Any<IEnumerable<ExcludedSymbol>>(), Arg.Any<CancellationToken>());
    Assert.True(result.ExcludedCount > 0);
}

[Fact]
public async Task DryRunDoesNotPersistRejections()
{
    var service = MakeService();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

    profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
    chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
             .Returns(new[] { MakeLegacyChunk("The axis homes when MoveLinear runs.") });

    var result = await service.RescrubAsync(chunkRepo,
                                            profileRepo,
                                            indexRepo,
                                            bm25ShardRepo,
                                            excludedRepo,
                                            "lib",
                                            "1.0",
                                            new RescrubOptions { DryRun = true },
                                            TestContext.Current.CancellationToken
                                           );

    await excludedRepo.DidNotReceive().DeleteAllForLibraryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    await excludedRepo.DidNotReceive().UpsertManyAsync(Arg.Any<IEnumerable<ExcludedSymbol>>(), Arg.Any<CancellationToken>());
    // ExcludedCount still computed (so DryRun reports what would have been excluded)
    Assert.True(result.ExcludedCount > 0);
}

[Fact]
public async Task EmitsHintsWhenRatioAndCountThresholdsMet()
{
    var service = MakeService();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

    profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
    // Build a corpus that produces many rejected tokens — each chunk has
    // distinct prose noise words. 30 distinct noise tokens easily clear
    // both the 20-absolute and 5%-ratio thresholds.
    var noiseChunks = Enumerable.Range(0, 30)
                                .Select(i => MakeLegacyChunk($"alpha{i} beta{i} gamma{i} delta{i} epsilon{i}."))
                                .ToArray();
    chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(noiseChunks);

    var result = await service.RescrubAsync(chunkRepo,
                                            profileRepo,
                                            indexRepo,
                                            bm25ShardRepo,
                                            excludedRepo,
                                            "lib",
                                            "1.0",
                                            new RescrubOptions(),
                                            TestContext.Current.CancellationToken
                                           );

    Assert.True(result.ExcludedCount >= 20);
    Assert.NotEmpty(result.Hints);
    Assert.Contains(result.Hints, h => h.Contains("list_excluded_symbols"));
}

[Fact]
public async Task SuppressesHintsBelowAbsoluteFloor()
{
    // Single chunk with a couple of stop words — well below the 20-token
    // absolute floor. No hints even if the ratio happens to clear 5%.
    var service = MakeService();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

    profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
    chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
             .Returns(new[] { MakeLegacyChunk("The axis homes.") });

    var result = await service.RescrubAsync(chunkRepo,
                                            profileRepo,
                                            indexRepo,
                                            bm25ShardRepo,
                                            excludedRepo,
                                            "lib",
                                            "1.0",
                                            new RescrubOptions(),
                                            TestContext.Current.CancellationToken
                                           );

    Assert.Empty(result.Hints);
}
```

You will also need to add `using DocRAG.Core.Models;` if the file doesn't already pull it (`MakeProfile` already constructs a `LibraryProfile`, so it does — leave it).

- [ ] **Step 11.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~RescrubServiceTests" -v minimal`
Expected: 4 new tests fail to compile (`RescrubAsync` doesn't accept `excludedRepo`).

- [ ] **Step 11.3: Update existing `RescrubServiceTests` calls**

The four pre-existing tests (`ReturnsReconNeededWhenProfileMissing`, `DryRunDoesNotWriteChunks`, `BumpsParserVersionAndPersistsChunks`, plus any other test in the file) currently call `service.RescrubAsync(chunkRepo, profileRepo, indexRepo, bm25ShardRepo, ...)`. Update each call to insert an `excludedRepo` substitute as the fifth positional argument:

For each existing test, add this near the top of the test body:

```csharp
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
```

Then change the existing call from:
```csharp
    var result = await service.RescrubAsync(chunkRepo,
                                            profileRepo,
                                            indexRepo,
                                            bm25ShardRepo,
                                            "lib",
                                            "1.0",
                                            new RescrubOptions(),
                                            TestContext.Current.CancellationToken
                                           );
```
to:
```csharp
    var result = await service.RescrubAsync(chunkRepo,
                                            profileRepo,
                                            indexRepo,
                                            bm25ShardRepo,
                                            excludedRepo,
                                            "lib",
                                            "1.0",
                                            new RescrubOptions(),
                                            TestContext.Current.CancellationToken
                                           );
```

Apply this to every existing call to `RescrubAsync` in the test file.

- [ ] **Step 11.4: Update `RescrubService.RescrubAsync` signature and logic**

Modify `DocRAG.Ingestion/Recon/RescrubService.cs`:

Add the new repo parameter to the public `RescrubAsync` signature (after `bm25ShardRepo` and before `libraryId`):

Old:
```csharp
    public async Task<RescrubResult> RescrubAsync(IChunkRepository chunkRepo,
                                                  ILibraryProfileRepository profileRepo,
                                                  ILibraryIndexRepository indexRepo,
                                                  IBm25ShardRepository bm25ShardRepo,
                                                  string libraryId,
                                                  string version,
                                                  RescrubOptions options,
                                                  CancellationToken ct = default)
```

New:
```csharp
    public async Task<RescrubResult> RescrubAsync(IChunkRepository chunkRepo,
                                                  ILibraryProfileRepository profileRepo,
                                                  ILibraryIndexRepository indexRepo,
                                                  IBm25ShardRepository bm25ShardRepo,
                                                  IExcludedSymbolsRepository excludedRepo,
                                                  string libraryId,
                                                  string version,
                                                  RescrubOptions options,
                                                  CancellationToken ct = default)
```

Add the same parameter to `RunRescrubAsync` and propagate it. Then thread it through to the chunk-processing loop.

Replace the existing `RunRescrubAsync` body so it (a) builds an accumulator after loading chunks, (b) feeds rejections into it during `ProcessChunksAsync`, and (c) persists + computes hints at the end. Full replacement for the method body (everything inside `RunRescrubAsync`):

Old (the existing `RunRescrubAsync` body):

```csharp
    private async Task<RescrubResult> RunRescrubAsync(IChunkRepository chunkRepo,
                                                      ILibraryIndexRepository indexRepo,
                                                      IBm25ShardRepository bm25ShardRepo,
                                                      string libraryId,
                                                      string version,
                                                      LibraryProfile profile,
                                                      RescrubOptions options,
                                                      CancellationToken ct)
    {
        var existingIndex = await indexRepo.GetAsync(libraryId, version, ct);
        var chunks = await chunkRepo.GetChunksAsync(libraryId, version, ct);
        var scoped = options.MaxChunks.HasValue ? chunks.Take(options.MaxChunks.Value).ToList() : chunks.ToList();

        var doReclassify = ResolveReClassify(options.ReClassify, profile, existingIndex);
        var corpus = BuildCorpusContext(scoped);
        var boundaryIssues = options.BoundaryAudit ? ChunkBoundaryAudit.CountIssues(scoped) : 0;

        var processedDiffs = await ProcessChunksAsync(chunkRepo,
                                                      scoped,
                                                      profile,
                                                      corpus,
                                                      doReclassify,
                                                      options.DryRun,
                                                      ct
                                                     );

        var indexesBuilt = false;
        if (options.RebuildIndexes && !options.DryRun)
        {
            await PersistLibraryIndexAsync(indexRepo, bm25ShardRepo, libraryId, version, profile, corpus, scoped, ct);
            indexesBuilt = true;
        }

        var sample = options.DryRun
                         ? processedDiffs
                         : processedDiffs.Take(SampleDiffsCount).ToList();

        var result = new RescrubResult
                         {
                             LibraryId = libraryId,
                             Version = version,
                             Processed = scoped.Count,
                             Changed = processedDiffs.Count,
                             BoundaryIssues = boundaryIssues,
                             DidReclassify = doReclassify,
                             IndexesBuilt = indexesBuilt,
                             DryRun = options.DryRun,
                             SampleDiffs = sample
                         };

        mLogger.LogInformation("Rescrub {Library}/{Version}: processed={Processed}, changed={Changed}, reclassify={Reclassify}, dryRun={DryRun}",
                               libraryId,
                               version,
                               scoped.Count,
                               processedDiffs.Count,
                               doReclassify,
                               options.DryRun
                              );

        return result;
    }
```

New (full replacement):

```csharp
    private async Task<RescrubResult> RunRescrubAsync(IChunkRepository chunkRepo,
                                                      ILibraryIndexRepository indexRepo,
                                                      IBm25ShardRepository bm25ShardRepo,
                                                      IExcludedSymbolsRepository excludedRepo,
                                                      string libraryId,
                                                      string version,
                                                      LibraryProfile profile,
                                                      RescrubOptions options,
                                                      CancellationToken ct)
    {
        var existingIndex = await indexRepo.GetAsync(libraryId, version, ct);
        var chunks = await chunkRepo.GetChunksAsync(libraryId, version, ct);
        var scoped = options.MaxChunks.HasValue ? chunks.Take(options.MaxChunks.Value).ToList() : chunks.ToList();

        var doReclassify = ResolveReClassify(options.ReClassify, profile, existingIndex);
        var corpus = BuildCorpusContext(scoped);
        var boundaryIssues = options.BoundaryAudit ? ChunkBoundaryAudit.CountIssues(scoped) : 0;

        var accumulator = new RejectionAccumulator(libraryId, version, Math.Max(scoped.Count, 1));
        var keptCount = 0;

        var processedDiffs = await ProcessChunksAsync(chunkRepo,
                                                      scoped,
                                                      profile,
                                                      corpus,
                                                      doReclassify,
                                                      options.DryRun,
                                                      accumulator,
                                                      keptObserved: count => keptCount += count,
                                                      ct
                                                     );

        var excludedEntries = accumulator.Build();
        var excludedCount = excludedEntries.Count;

        var indexesBuilt = false;
        if (options.RebuildIndexes && !options.DryRun)
        {
            await PersistLibraryIndexAsync(indexRepo, bm25ShardRepo, libraryId, version, profile, corpus, scoped, ct);
            indexesBuilt = true;
        }

        if (!options.DryRun)
        {
            await excludedRepo.DeleteAllForLibraryAsync(libraryId, version, ct);
            await excludedRepo.UpsertManyAsync(excludedEntries, ct);
        }

        var sample = options.DryRun
                         ? processedDiffs
                         : processedDiffs.Take(SampleDiffsCount).ToList();
        var hints = BuildHints(libraryId, version, scoped.Count, keptCount, excludedCount);

        var result = new RescrubResult
                         {
                             LibraryId = libraryId,
                             Version = version,
                             Processed = scoped.Count,
                             Changed = processedDiffs.Count,
                             BoundaryIssues = boundaryIssues,
                             DidReclassify = doReclassify,
                             IndexesBuilt = indexesBuilt,
                             DryRun = options.DryRun,
                             SampleDiffs = sample,
                             ExcludedCount = excludedCount,
                             Hints = hints
                         };

        mLogger.LogInformation("Rescrub {Library}/{Version}: processed={Processed}, changed={Changed}, excluded={Excluded}, reclassify={Reclassify}, dryRun={DryRun}",
                               libraryId,
                               version,
                               scoped.Count,
                               processedDiffs.Count,
                               excludedCount,
                               doReclassify,
                               options.DryRun
                              );

        return result;
    }
```

Update `RescrubAsync` (the public entry) to forward the new repo:

Old (lines 49–75 of the file):
```csharp
    public async Task<RescrubResult> RescrubAsync(IChunkRepository chunkRepo,
                                                  ILibraryProfileRepository profileRepo,
                                                  ILibraryIndexRepository indexRepo,
                                                  IBm25ShardRepository bm25ShardRepo,
                                                  string libraryId,
                                                  string version,
                                                  RescrubOptions options,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunkRepo);
        ArgumentNullException.ThrowIfNull(profileRepo);
        ArgumentNullException.ThrowIfNull(indexRepo);
        ArgumentNullException.ThrowIfNull(bm25ShardRepo);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(options);

        var profile = await profileRepo.GetAsync(libraryId, version, ct);
        RescrubResult result;

        if (profile == null)
            result = new RescrubResult { LibraryId = libraryId, Version = version, ReconNeeded = true };
        else
            result = await RunRescrubAsync(chunkRepo, indexRepo, bm25ShardRepo, libraryId, version, profile, options, ct);

        return result;
    }
```

New:
```csharp
    public async Task<RescrubResult> RescrubAsync(IChunkRepository chunkRepo,
                                                  ILibraryProfileRepository profileRepo,
                                                  ILibraryIndexRepository indexRepo,
                                                  IBm25ShardRepository bm25ShardRepo,
                                                  IExcludedSymbolsRepository excludedRepo,
                                                  string libraryId,
                                                  string version,
                                                  RescrubOptions options,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunkRepo);
        ArgumentNullException.ThrowIfNull(profileRepo);
        ArgumentNullException.ThrowIfNull(indexRepo);
        ArgumentNullException.ThrowIfNull(bm25ShardRepo);
        ArgumentNullException.ThrowIfNull(excludedRepo);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(options);

        var profile = await profileRepo.GetAsync(libraryId, version, ct);
        RescrubResult result;

        if (profile == null)
            result = new RescrubResult { LibraryId = libraryId, Version = version, ReconNeeded = true };
        else
            result = await RunRescrubAsync(chunkRepo, indexRepo, bm25ShardRepo, excludedRepo, libraryId, version, profile, options, ct);

        return result;
    }
```

Update `ProcessChunksAsync` to accept the accumulator + kept-observed callback and feed both:

Old (the existing `ProcessChunksAsync` body):
```csharp
    private async Task<List<RescrubDiff>> ProcessChunksAsync(IChunkRepository chunkRepo,
                                                             IReadOnlyList<DocChunk> chunks,
                                                             LibraryProfile profile,
                                                             CorpusContext corpus,
                                                             bool doReclassify,
                                                             bool dryRun,
                                                             CancellationToken ct)
    {
        var diffs = new List<RescrubDiff>();
        var updated = new List<DocChunk>();

        foreach(var chunk in chunks)
        {
            var (newChunk, diff) = await RescrubOneAsync(chunk, profile, corpus, doReclassify, ct);
            if (diff != null)
            {
                diffs.Add(diff);
                if (!dryRun)
                    updated.Add(newChunk);
            }
        }

        if (!dryRun && updated.Count > 0)
            await chunkRepo.UpsertChunksAsync(updated, ct);

        return diffs;
    }
```

New:
```csharp
    private async Task<List<RescrubDiff>> ProcessChunksAsync(IChunkRepository chunkRepo,
                                                             IReadOnlyList<DocChunk> chunks,
                                                             LibraryProfile profile,
                                                             CorpusContext corpus,
                                                             bool doReclassify,
                                                             bool dryRun,
                                                             RejectionAccumulator accumulator,
                                                             Action<int> keptObserved,
                                                             CancellationToken ct)
    {
        var diffs = new List<RescrubDiff>();
        var updated = new List<DocChunk>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var oneResult = await RescrubOneAsync(chunk, profile, corpus, doReclassify, ct);
            keptObserved(oneResult.Extracted.Symbols.Count);
            foreach(var rejected in oneResult.Extracted.Rejected)
                accumulator.Record(rejected, i, chunk.Content);

            if (oneResult.Diff != null)
            {
                diffs.Add(oneResult.Diff);
                if (!dryRun)
                    updated.Add(oneResult.Chunk);
            }
        }

        if (!dryRun && updated.Count > 0)
            await chunkRepo.UpsertChunksAsync(updated, ct);

        return diffs;
    }
```

`RescrubOneAsync` currently returns `(DocChunk, RescrubDiff?)`; widen it to also expose the `ExtractedSymbols` so the accumulator can read the rejections. Old:

```csharp
    private async Task<(DocChunk Chunk, RescrubDiff? Diff)> RescrubOneAsync(DocChunk chunk,
                                                                            LibraryProfile profile,
                                                                            CorpusContext corpus,
                                                                            bool doReclassify,
                                                                            CancellationToken ct)
    {
        var extracted = mExtractor.Extract(chunk.Content, profile, corpus);
        ...
        return (newChunk, diff);
    }
```

New:

```csharp
    private async Task<RescrubOneResult> RescrubOneAsync(DocChunk chunk,
                                                          LibraryProfile profile,
                                                          CorpusContext corpus,
                                                          bool doReclassify,
                                                          CancellationToken ct)
    {
        var extracted = mExtractor.Extract(chunk.Content, profile, corpus);

        var newCategory = chunk.Category;
        if (doReclassify)
            newCategory = await ReclassifyAsync(chunk, profile, ct);

        var changed = HasChanged(chunk, extracted, newCategory);
        DocChunk newChunk = chunk;
        RescrubDiff? diff = null;

        if (changed)
        {
            newChunk = chunk with
                           {
                               Symbols = extracted.Symbols,
                               QualifiedName = extracted.PrimaryQualifiedName ?? chunk.QualifiedName,
                               ParserVersion = ParserVersionInfo.Current,
                               Category = newCategory
                           };

            diff = new RescrubDiff
                       {
                           ChunkId = chunk.Id,
                           OldQualifiedName = chunk.QualifiedName,
                           NewQualifiedName = newChunk.QualifiedName,
                           OldSymbolCount = chunk.Symbols.Count,
                           NewSymbolCount = extracted.Symbols.Count,
                           OldCategory = chunk.Category == newCategory ? null : chunk.Category,
                           NewCategory = chunk.Category == newCategory ? null : newCategory
                       };
        }

        var result = new RescrubOneResult
                         {
                             Chunk = newChunk,
                             Diff = diff,
                             Extracted = extracted
                         };
        return result;
    }

    private sealed record RescrubOneResult
    {
        public required DocChunk Chunk { get; init; }
        public required RescrubDiff? Diff { get; init; }
        public required ExtractedSymbols Extracted { get; init; }
    }
```

Add a `BuildHints` helper (place it next to the other private statics, near the bottom of the file before the regex constants):

```csharp
    private static IReadOnlyList<string> BuildHints(string libraryId,
                                                     string version,
                                                     int processedChunks,
                                                     int keptCount,
                                                     int excludedCount)
    {
        var totalCandidates = keptCount + excludedCount;
        var ratio = totalCandidates > 0 ? (double) excludedCount / totalCandidates : 0;
        var trigger = excludedCount >= HintMinAbsoluteExclusions
                   && ratio >= HintMinExclusionRatio;
        IReadOnlyList<string> result = trigger
                                            ? new[]
                                                  {
                                                      $"Rescrub complete: {processedChunks} chunks, {keptCount} candidate tokens kept, {excludedCount} excluded as likely noise.",
                                                      "If list_classes/list_functions output looks off, refine per-library:",
                                                      $"  list_excluded_symbols(library='{libraryId}', version='{version}') — review rejections with sample sentences",
                                                      "  add_to_likely_symbols(...) — promote tokens that ARE real symbols",
                                                      "  add_to_stoplist(...) — demote tokens that are noise",
                                                      "  Then call rescrub_library again to apply.",
                                                      "These steps are OPTIONAL — only worth running if symbol coverage looks wrong on spot-check."
                                                  }
                                            : Array.Empty<string>();
        return result;
    }
```

Add the const declarations near the existing const block at the bottom:

```csharp
    private const int HintMinAbsoluteExclusions = 20;
    private const double HintMinExclusionRatio = 0.05;
```

- [ ] **Step 11.5: Update `RescrubTools` to inject the new repo**

Modify `DocRAG.Mcp/Tools/RescrubTools.cs` — the `RescrubLibrary` method body. Add the new repo lookup after `bm25ShardRepo`:

Old:
```csharp
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25ShardRepo = repositoryFactory.GetBm25ShardRepository(profile);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                library,
                                                version,
                                                options,
                                                ct
                                               );
```

New:
```csharp
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25ShardRepo = repositoryFactory.GetBm25ShardRepository(profile);
        var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                library,
                                                version,
                                                options,
                                                ct
                                               );
```

- [ ] **Step 11.6: Run the full test suite**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj -v minimal`
Expected: PASS. 4 new RescrubServiceTests, plus all pre-existing tests after their `RescrubAsync` calls were updated.

- [ ] **Step 11.7: Commit**

Write `e:/tmp/msg.txt`:
```
Wire RescrubService to capture and persist rejections

The rescrub pass now feeds extractor rejections through a
RejectionAccumulator and writes the resulting ExcludedSymbol
records to library_excluded_symbols, deleting any prior rows
first so we never accumulate stale state. Hints are emitted on
the result when ≥5% of candidates were excluded AND ≥20 tokens
were excluded in absolute terms — quiet libraries stay quiet.
RescrubTools forwards the new repository through the existing
factory pattern.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Recon/RescrubService.cs DocRAG.Mcp/Tools/RescrubTools.cs DocRAG.Tests/Recon/RescrubServiceTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 12: Stoplist carry-forward in `LibraryProfileService`

**Files:**
- Modify: `DocRAG.Ingestion/Recon/LibraryProfileService.cs`
- Modify: `DocRAG.Tests/Recon/LibraryProfileServiceTests.cs`

- [ ] **Step 12.1: Write failing tests**

Append to `DocRAG.Tests/Recon/LibraryProfileServiceTests.cs` (before the closing `}` of the class):

```csharp
[Fact]
public async Task SaveCarriesForwardStoplistFromPriorVersionWhenEmpty()
{
    var repo = Substitute.For<ILibraryProfileRepository>();
    var priorProfile = MakeProfileWithStoplist(version: "1.0", stoplist: ["along", "data"]);
    repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { priorProfile });

    var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
    var newProfile = MakeProfileWithStoplist(version: "1.1", stoplist: []);

    var saved = await service.SaveAsync(repo, newProfile, TestContext.Current.CancellationToken);

    Assert.Equal(new[] { "along", "data" }, saved.Stoplist);
    await repo.Received(1).UpsertAsync(Arg.Is<LibraryProfile>(p => p.Stoplist.SequenceEqual(new[] { "along", "data" })),
                                       Arg.Any<CancellationToken>());
}

[Fact]
public async Task SaveDoesNotOverrideNonEmptyStoplist()
{
    var repo = Substitute.For<ILibraryProfileRepository>();
    var priorProfile = MakeProfileWithStoplist(version: "1.0", stoplist: ["along", "data"]);
    repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { priorProfile });

    var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
    var newProfile = MakeProfileWithStoplist(version: "1.1", stoplist: ["enumerator"]);

    var saved = await service.SaveAsync(repo, newProfile, TestContext.Current.CancellationToken);

    Assert.Equal(new[] { "enumerator" }, saved.Stoplist);
}

[Fact]
public async Task SaveLeavesStoplistEmptyWhenNoPriorVersionExists()
{
    var repo = Substitute.For<ILibraryProfileRepository>();
    repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryProfile>());

    var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
    var newProfile = MakeProfileWithStoplist(version: "1.0", stoplist: []);

    var saved = await service.SaveAsync(repo, newProfile, TestContext.Current.CancellationToken);

    Assert.Empty(saved.Stoplist);
}

private static LibraryProfile MakeProfileWithStoplist(string version, IReadOnlyList<string> stoplist) =>
    new()
        {
            Id = $"aerotech-aeroscript/{version}",
            LibraryId = "aerotech-aeroscript",
            Version = version,
            Source = "test",
            Stoplist = stoplist
        };
```

Also add `using DocRAG.Core.Interfaces;` and `using NSubstitute;` and `using Microsoft.Extensions.Logging.Abstractions;` to the file's `#region Usings` block if not already present.

- [ ] **Step 12.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~LibraryProfileServiceTests" -v minimal`
Expected: 3 new tests fail.

- [ ] **Step 12.3: Implement carry-forward**

Modify `DocRAG.Ingestion/Recon/LibraryProfileService.cs`:

Replace the body of `SaveAsync`:

Old:
```csharp
    public async Task<LibraryProfile> SaveAsync(ILibraryProfileRepository repository,
                                                LibraryProfile profile,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(profile);

        Validate(profile);

        var normalized = Normalize(profile);
        await repository.UpsertAsync(normalized, ct);
        mLogger.LogInformation("Saved library profile for {LibraryId}/{Version} (source={Source}, confidence={Confidence:F2})",
                               normalized.LibraryId,
                               normalized.Version,
                               normalized.Source,
                               normalized.Confidence
                              );
        return normalized;
    }
```

New:
```csharp
    public async Task<LibraryProfile> SaveAsync(ILibraryProfileRepository repository,
                                                LibraryProfile profile,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(profile);

        Validate(profile);

        var normalized = Normalize(profile);
        var withCarryForward = await ApplyStoplistCarryForwardAsync(repository, normalized, ct);

        await repository.UpsertAsync(withCarryForward, ct);
        mLogger.LogInformation("Saved library profile for {LibraryId}/{Version} (source={Source}, confidence={Confidence:F2}, stoplist={StoplistCount})",
                               withCarryForward.LibraryId,
                               withCarryForward.Version,
                               withCarryForward.Source,
                               withCarryForward.Confidence,
                               withCarryForward.Stoplist.Count
                              );
        return withCarryForward;
    }

    /// <summary>
    ///     If the incoming profile has an empty Stoplist and a prior profile
    ///     for the same LibraryId (any other version) has a non-empty
    ///     Stoplist, copy the most-recent prior Stoplist forward. Lets the
    ///     LLM's curation work survive a library version bump without
    ///     re-doing it. Non-empty incoming Stoplists are never overridden.
    /// </summary>
    private static async Task<LibraryProfile> ApplyStoplistCarryForwardAsync(ILibraryProfileRepository repository,
                                                                              LibraryProfile profile,
                                                                              CancellationToken ct)
    {
        LibraryProfile result = profile;
        if (profile.Stoplist.Count == 0)
        {
            var all = await repository.ListAllAsync(ct);
            var prior = all.Where(p => string.Equals(p.LibraryId, profile.LibraryId, StringComparison.Ordinal)
                                    && !string.Equals(p.Version, profile.Version, StringComparison.Ordinal)
                                    && p.Stoplist.Count > 0)
                           .OrderByDescending(p => p.CreatedUtc)
                           .FirstOrDefault();
            if (prior != null)
                result = profile with { Stoplist = prior.Stoplist };
        }
        return result;
    }
```

- [ ] **Step 12.4: Run tests to verify pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~LibraryProfileServiceTests" -v minimal`
Expected: PASS. All 3 new tests + existing.

- [ ] **Step 12.5: Commit**

Write `e:/tmp/msg.txt`:
```
Carry forward LibraryProfile.Stoplist on version bump

When a new (library, version) profile is saved with an empty
Stoplist, the most-recent prior version's Stoplist is copied
forward so the LLM's curation work survives library upgrades.
A non-empty incoming Stoplist is never overridden.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Ingestion/Recon/LibraryProfileService.cs DocRAG.Tests/Recon/LibraryProfileServiceTests.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 13: `SymbolManagementTools` — the three MCP tools

**Files:**
- Create: `DocRAG.Mcp/Tools/SymbolManagementTools.cs`
- Create: `DocRAG.Tests/Mcp/SymbolManagementToolsTests.cs`

- [ ] **Step 13.1: Write failing tests**

```csharp
// SymbolManagementToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Mcp.Tools;
using NSubstitute;

#endregion

namespace DocRAG.Tests.Mcp;

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
        var factory = Substitute.For<RepositoryFactory>(args: null!);
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
```

Note: the `RepositoryFactory` mock takes `args: null!` because its constructor takes a `DocRagDbContextFactory`. NSubstitute will create a stand-in via `Substitute.For<RepositoryFactory>(args: null!)`. If that substitution is rejected at runtime (NSubstitute requires concrete classes to be virtual), make `RepositoryFactory.GetLibraryProfileRepository` and `GetExcludedSymbolsRepository` virtual. Both already exist; mark them `virtual` if needed.

Check: in Step 4.3 you added `GetExcludedSymbolsRepository` without `virtual`. Look at `GetLibraryRepository` — it's already `virtual`. **Mark `GetExcludedSymbolsRepository` as `virtual`** when adding it (and confirm `GetLibraryProfileRepository` is `virtual`; if not, mark it too at this step). The pattern in the codebase is: any factory method that tests need to mock should be `virtual`.

If `GetLibraryProfileRepository` is NOT currently virtual, edit it now to add `virtual` and stage that change with this commit.

- [ ] **Step 13.2: Run failing tests**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~SymbolManagementToolsTests" -v minimal`
Expected: FAIL — `SymbolManagementTools` doesn't exist; possibly compile error from non-virtual factory methods.

- [ ] **Step 13.3: Make factory methods virtual if needed**

Edit `RepositoryFactory.cs`. For each of `GetLibraryProfileRepository`, `GetExcludedSymbolsRepository`, add the `virtual` modifier:

Old (example):
```csharp
    public ILibraryProfileRepository GetLibraryProfileRepository(string? profile = null)
```

New:
```csharp
    public virtual ILibraryProfileRepository GetLibraryProfileRepository(string? profile = null)
```

Same for `GetExcludedSymbolsRepository`.

- [ ] **Step 13.4: Implement `SymbolManagementTools`**

```csharp
// SymbolManagementTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools that let a calling LLM review and refine the symbol
///     extractor's per-library decisions:
///       — list_excluded_symbols: see what was rejected, with sample
///         sentences pulled from the corpus.
///       — add_to_likely_symbols: promote a token (extractor will keep it
///         even when it lacks structural signal).
///       — add_to_stoplist: demote a token (extractor will reject it
///         regardless of signal).
///
///     All three are optional. The rescrub_library tool's Hints field
///     suggests using them when the rejection count looks suspicious.
/// </summary>
[McpServerToolType]
public static class SymbolManagementTools
{
    [McpServerTool(Name = "list_excluded_symbols")]
    [Description("Return the per-(library, version) tokens that the symbol extractor " +
                 "rejected during the last rescrub, with the reason and a few sample " +
                 "sentences. Use to triage which rejections are correct (noise) and " +
                 "which to override via add_to_likely_symbols.")]
    public static async Task<string> ListExcludedSymbols(RepositoryFactory repositoryFactory,
                                                          [Description("Library identifier")]
                                                          string library,
                                                          [Description("Library version")]
                                                          string version,
                                                          [Description("Optional reason filter (GlobalStoplist, LibraryStoplist, Unit, BelowMinLength, LikelyAbbreviation, NoStructureSignal).")]
                                                          SymbolRejectionReason? reason = null,
                                                          [Description("Maximum entries to return. Default 50.")]
                                                          int limit = DefaultListLimit,
                                                          [Description("Optional database profile name")]
                                                          string? profile = null,
                                                          CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var libraryProfile = await profileRepo.GetAsync(library, version, ct);

        string result;
        if (libraryProfile == null)
            result = JsonSerializer.Serialize(new { ReconNeeded = true, Library = library, Version = version }, smJsonOptions);
        else
        {
            var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
            var items = await excludedRepo.ListAsync(library, version, reason, limit, ct);
            var total = await excludedRepo.CountAsync(library, version, ct);
            result = JsonSerializer.Serialize(new
                                                  {
                                                      Library = library,
                                                      Version = version,
                                                      TotalExcluded = total,
                                                      Returned = items.Count,
                                                      Items = items.Select(i => new
                                                                                    {
                                                                                        i.Name,
                                                                                        Reason = i.Reason.ToString(),
                                                                                        i.ChunkCount,
                                                                                        i.SampleSentences
                                                                                    })
                                                  }, smJsonOptions);
        }
        return result;
    }

    [McpServerTool(Name = "add_to_likely_symbols")]
    [Description("Promote one or more tokens to LibraryProfile.LikelySymbols so the " +
                 "extractor keeps them even without other structural signal. Auto-removes " +
                 "any case-equivalent variant from LibraryProfile.Stoplist (last call wins). " +
                 "Returns a summary of what changed plus a hint to call rescrub_library.")]
    public static async Task<string> AddToLikelySymbols(RepositoryFactory repositoryFactory,
                                                         [Description("Library identifier")]
                                                         string library,
                                                         [Description("Library version")]
                                                         string version,
                                                         [Description("Tokens to promote.")]
                                                         IReadOnlyList<string> names,
                                                         [Description("Optional database profile name")]
                                                         string? profile = null,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            throw new ArgumentException("names must contain at least one entry", nameof(names));

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var libraryProfile = await profileRepo.GetAsync(library, version, ct);

        string result;
        if (libraryProfile == null)
            result = JsonSerializer.Serialize(new { ReconNeeded = true, Library = library, Version = version }, smJsonOptions);
        else
        {
            var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
            var alreadyInLikely = libraryProfile.LikelySymbols.Where(nameSet.Contains).ToList();
            var promoted = nameSet.Where(n => !libraryProfile.LikelySymbols.Contains(n, StringComparer.Ordinal)).ToList();

            var newLikely = libraryProfile.LikelySymbols.Concat(promoted).ToList();
            var newStoplist = libraryProfile.Stoplist
                                            .Where(s => !nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                            .ToList();
            var removedFromStoplist = libraryProfile.Stoplist
                                                    .Where(s => nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                                    .ToList();

            var updated = libraryProfile with
                              {
                                  LikelySymbols = newLikely,
                                  Stoplist = newStoplist
                              };
            await profileRepo.UpsertAsync(updated, ct);

            var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
            await excludedRepo.RemoveAsync(library, version, names, ct);

            result = JsonSerializer.Serialize(new
                                                  {
                                                      Library = library,
                                                      Version = version,
                                                      Promoted = promoted,
                                                      AlreadyInLikelySymbols = alreadyInLikely,
                                                      RemovedFromStoplist = removedFromStoplist,
                                                      Hints = new[] { "Call rescrub_library to apply the changes." }
                                                  }, smJsonOptions);
        }
        return result;
    }

    [McpServerTool(Name = "add_to_stoplist")]
    [Description("Demote one or more tokens to LibraryProfile.Stoplist so the extractor " +
                 "rejects them regardless of structural signal. Case-insensitive — adding " +
                 "'foo' blocks 'Foo', 'FOO', etc. Auto-removes case-equivalent entries " +
                 "from LibraryProfile.LikelySymbols (last call wins). Returns a summary " +
                 "of what changed plus a hint to call rescrub_library.")]
    public static async Task<string> AddToStoplist(RepositoryFactory repositoryFactory,
                                                    [Description("Library identifier")]
                                                    string library,
                                                    [Description("Library version")]
                                                    string version,
                                                    [Description("Tokens to demote.")]
                                                    IReadOnlyList<string> names,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            throw new ArgumentException("names must contain at least one entry", nameof(names));

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var libraryProfile = await profileRepo.GetAsync(library, version, ct);

        string result;
        if (libraryProfile == null)
            result = JsonSerializer.Serialize(new { ReconNeeded = true, Library = library, Version = version }, smJsonOptions);
        else
        {
            var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
            var alreadyInStoplist = libraryProfile.Stoplist
                                                  .Where(s => nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                                  .ToList();
            var demoted = nameSet.Where(n => !libraryProfile.Stoplist.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

            var newStoplist = libraryProfile.Stoplist.Concat(demoted).ToList();
            var newLikely = libraryProfile.LikelySymbols
                                          .Where(s => !nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                          .ToList();
            var removedFromLikely = libraryProfile.LikelySymbols
                                                  .Where(s => nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                                  .ToList();

            var updated = libraryProfile with
                              {
                                  Stoplist = newStoplist,
                                  LikelySymbols = newLikely
                              };
            await profileRepo.UpsertAsync(updated, ct);

            var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
            await excludedRepo.RemoveAsync(library, version, names, ct);

            result = JsonSerializer.Serialize(new
                                                  {
                                                      Library = library,
                                                      Version = version,
                                                      Demoted = demoted,
                                                      AlreadyInStoplist = alreadyInStoplist,
                                                      RemovedFromLikelySymbols = removedFromLikely,
                                                      Hints = new[] { "Call rescrub_library to apply the changes." }
                                                  }, smJsonOptions);
        }
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new()
                                                                      {
                                                                          WriteIndented = true,
                                                                          Converters = { new JsonStringEnumConverter() }
                                                                      };

    private const int DefaultListLimit = 50;
}
```

- [ ] **Step 13.5: Run tests to verify pass**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter "FullyQualifiedName~SymbolManagementToolsTests" -v minimal`
Expected: PASS. All 7 tests.

- [ ] **Step 13.6: Run the full suite**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj -v minimal`
Expected: PASS — every test, including pre-existing ones.

- [ ] **Step 13.7: Commit**

Write `e:/tmp/msg.txt`:
```
Add SymbolManagementTools — list / add_to_likely / add_to_stoplist

Three MCP tools complete the LLM symbol-management flow:
- list_excluded_symbols surfaces rejections with reason and
  sample sentences, sorted by chunk-count descending.
- add_to_likely_symbols promotes tokens, removing any
  case-equivalent stoplist entry as the override.
- add_to_stoplist demotes tokens, removing any
  case-equivalent LikelySymbols entry.

Both mutation tools also wipe matching entries from
library_excluded_symbols so subsequent list calls reflect the
new state without waiting for the next rescrub. Empty `names`
fails fast with ArgumentException.

RepositoryFactory.GetLibraryProfileRepository and the new
GetExcludedSymbolsRepository are now virtual to support the
NSubstitute-based unit tests.
```

Run:
```
git -C e:/GitHub/DocRAG add DocRAG.Mcp/Tools/SymbolManagementTools.cs DocRAG.Tests/Mcp/SymbolManagementToolsTests.cs DocRAG.Database/Repositories/RepositoryFactory.cs
git -C e:/GitHub/DocRAG commit -F e:/tmp/msg.txt
```

---

## Task 14: End-to-end verification against side-by-side server

Manual verification — no commits, no code changes.

- [ ] **Step 14.1: Build clean**

Run: `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
Expected: 0 errors, 0 warnings.

- [ ] **Step 14.2: Run full test suite**

Run: `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --no-build -v minimal`
Expected: 100% pass.

- [ ] **Step 14.3: Start the side-by-side server in the background**

Run (background):
```
dotnet run --project e:/GitHub/DocRAG/DocRAG.Mcp --launch-profile DevSideBySide --no-build -c Debug
```

Wait for `/health` on port 6101 to report healthy:
```
curl http://localhost:6101/health
```
Expected: `{"status":"healthy"}` or equivalent.

- [ ] **Step 14.4: Run rescrub against the aerotech corpus**

Send the MCP `rescrub_library` JSON-RPC call to `http://localhost:6101/mcp` with `library=aerotech-aeroscript`, `version=1.0`. Use whatever client is already configured (Inspector, the user's existing session, etc.).

Expected:
- Response includes `ExcludedCount` > 0.
- Response includes a `Hints` array with the management-tool advice (we know there's residual noise from prior rescrubs).

- [ ] **Step 14.5: List rejections**

Call `list_excluded_symbols(library='aerotech-aeroscript', version='1.0', limit=20)`.

Expected: residual noise tokens visible — at minimum some of `RealTek`, `_QUAD_COPPER`, `AFuwU`, `along`, `data`, `enumerator`, `integer` — each with a reason and 1-3 sample sentences.

- [ ] **Step 14.6: Demote four common-word noise tokens**

Call `add_to_stoplist(library='aerotech-aeroscript', version='1.0', names=['along','data','enumerator','integer'])`.

Expected: response shows `Demoted: ['along','data','enumerator','integer']`, `AlreadyInStoplist: []`, `RemovedFromLikelySymbols: []` (none of those should have been promoted).

- [ ] **Step 14.7: Re-run rescrub and confirm the four are now `LibraryStoplist`**

Run `rescrub_library` again. Then `list_excluded_symbols(library='aerotech-aeroscript', version='1.0', reason='LibraryStoplist')`.

Expected: the four demoted tokens appear with `Reason='LibraryStoplist'`.

- [ ] **Step 14.8: Confirm the noise enums are gone**

Call `list_enums(library='aerotech-aeroscript', version='1.0')`.

Expected: the four words no longer appear as enum entries.

- [ ] **Step 14.9: Stop the side-by-side server**

Kill the background process (Ctrl+C in the terminal where it runs, or `KillShell` if started via the agent's Bash tool).

---

## Self-Review

**Spec coverage** (every spec section has at least one task):
- Data model (enum, ExcludedSymbol, Stoplist field, Mongo collection + indexes) → Tasks 1, 2, 4.
- Stoplist profile-aware match → Task 5.
- SymbolExtractor returning rejections → Task 7.
- RejectedToken / ExtractedSymbols.Rejected → Task 6.
- SampleWindowExtractor → Task 8.
- RejectionAccumulator → Task 9.
- RescrubResult shape change → Task 10.
- RescrubService wiring + hint computation → Task 11.
- LibraryProfileService Stoplist carry-forward → Task 12.
- Three new MCP tools → Task 13.
- End-to-end verification → Task 14.

**Placeholder scan:** No "TBD"/"TODO"/"implement later"/"appropriate"/"as needed" remain. Every code step contains either complete code or an explicit instruction with the exact location to modify.

**Type consistency:**
- `SymbolRejectionReason` namespace = `DocRAG.Core.Enums` (Tasks 1, 7, 13).
- `ExcludedSymbol.MakeId(libraryId, version, name)` (Task 1) used by `RejectionAccumulator` (Task 9).
- `IExcludedSymbolsRepository` (Task 3) consumed verbatim by `RescrubService` (Task 11) and `SymbolManagementTools` (Task 13).
- `RepositoryFactory.GetExcludedSymbolsRepository` (Task 4) used by `RescrubTools` (Task 11) and `SymbolManagementTools` (Task 13). Marked `virtual` in Task 13.
- `StoplistMatch` enum (Task 5) consumed by `SymbolExtractor.GetRejectionReason` (Task 7).
- `RejectedToken` (Task 6) emitted by `SymbolExtractor` (Task 7) and consumed by `RejectionAccumulator` (Task 9).
- `RescrubResult.ExcludedCount` and `Hints` (Task 10) populated by `RescrubService.BuildHints` (Task 11) and asserted by tests (Task 11).

All cross-task references match.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-27-llm-symbol-management.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — Dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
