# MCP Tool UX Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the SaddleRAG MCP tool surface usable by a fresh LLM consumer — fix cold-start dead ends, add visibility into library health, expose rename/delete/cancel, consolidate the two scrape tools, surface "this URL probably isn't docs" via recon delegation, and collapse redundant listing/resume tools.

**Architecture:** Seven tracks (A–G) on the `feature/mcp-tool-ux` branch. Repository methods first (Track C foundation), then runtime/cancellation (F), surface consolidation (G), scrape consolidation (D), visibility (B), URL sanity (E), and cold-start orientation (A) last. All tracks share data-model touch-points but are otherwise independent. Each task ends with a commit so the implementation order can be reordered without merge pain.

**Tech Stack:** .NET 10, C# (Penske coding standards: m-prefix instance fields, sm-prefix statics, Allman braces, single-return, ArgumentException validation, async-suffix), MongoDB.Driver, ModelContextProtocol.Server attributes, xunit + NSubstitute for tests. Solution file is `SaddleRAG.slnx`.

**Key references the engineer should re-read before starting:**
- Spec: `docs/superpowers/specs/2026-04-27-mcp-tool-ux-design.md`
- CLAUDE.md (repo root) — no AI attribution in commits/PRs, ever
- Existing patterns: `SaddleRAG.Mcp/Tools/SymbolManagementTools.cs` (tool style), `SaddleRAG.Tests/Mcp/SymbolManagementToolsTests.cs` (test style), `SaddleRAG.Database/Repositories/LibraryRepository.cs` (repo style)

---

## File Structure

### Files created

- `SaddleRAG.Mcp/Tools/MutationTools.cs` — `rename_library`, `delete_library`, `delete_version`
- `SaddleRAG.Mcp/Tools/HealthTools.cs` — `get_library_health`, `get_dashboard_index`
- `SaddleRAG.Mcp/Tools/CancellationTools.cs` — `cancel_scrape`
- `SaddleRAG.Mcp/Tools/UrlCorrectionTools.cs` — `submit_url_correction`
- `SaddleRAG.Ingestion/Suspect/SuspectDetector.cs` — five heuristics
- `SaddleRAG.Ingestion/Suspect/SuspectReason.cs` — string constants
- `SaddleRAG.Tests/Mcp/MutationToolsTests.cs`
- `SaddleRAG.Tests/Mcp/HealthToolsTests.cs`
- `SaddleRAG.Tests/Mcp/CancellationToolsTests.cs`
- `SaddleRAG.Tests/Mcp/UrlCorrectionToolsTests.cs`
- `SaddleRAG.Tests/Mcp/ListSymbolsToolTests.cs`
- `SaddleRAG.Tests/Suspect/SuspectDetectorTests.cs`

### Files modified

- `SaddleRAG.Core/Enums/ScrapeJobStatus.cs` — add `Cancelled = 4`
- `SaddleRAG.Core/Enums/IngestStatus.cs` — add `UrlSuspect`, `InProgress`
- `SaddleRAG.Core/Models/LibraryVersionRecord.cs` — add `Suspect`, `SuspectReasons`, `LastSuspectEvaluatedAt`, `BoundaryIssuePct`
- `SaddleRAG.Core/Models/ScrapeJobRecord.cs` — add `LastProgressAt`, `CancelledAt`
- `SaddleRAG.Core/Interfaces/ILibraryRepository.cs` — add `RenameAsync`, `DeleteAsync`, `DeleteVersionAsync`, `SetSuspectAsync`, `ClearSuspectAsync`
- `SaddleRAG.Core/Interfaces/IChunkRepository.cs` — add `GetLanguageMixAsync`, `GetHostnameDistributionAsync`, `GetSampleTitlesAsync`
- `SaddleRAG.Core/Interfaces/IPageRepository.cs` — add `DeleteAsync`
- `SaddleRAG.Database/Repositories/LibraryRepository.cs` — implement new methods
- `SaddleRAG.Database/Repositories/ChunkRepository.cs` — implement new aggregations
- `SaddleRAG.Database/Repositories/PageRepository.cs` — implement `DeleteAsync`
- `SaddleRAG.Mcp/Tools/LibraryTools.cs` — replace 4 list_* tools with `list_symbols`, add empty-state hint to `list_libraries`
- `SaddleRAG.Mcp/Tools/IngestTools.cs` — add `IN_PROGRESS` and `URL_SUSPECT` branches to `ResolveStatus`, gate by `currentVersion!=null`/`Suspect`/`Running` lookups
- `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` — add `allowedUrlPatterns` / `excludedUrlPatterns` / `resume` to `scrape_docs`; remove `continue_scrape`
- `SaddleRAG.Mcp/Tools/IngestionTools.cs` — remove `scrape_library`
- `SaddleRAG.Mcp/Tools/RescrubTools.cs` — add `BoundaryHint` to output
- `SaddleRAG.Ingestion/ScrapeJobRunner.cs` — add CTS registry, `CancelAsync`, `LastProgressAt` updates
- `SaddleRAG.Ingestion/IngestionOrchestrator.cs` — call `SuspectDetector` inside `UpdateLibraryMetadataAsync`
- `SaddleRAG.Ingestion/Recon/RescrubService.cs` — persist `BoundaryIssuePct` to `LibraryVersionRecord`

### Note on the spec

Spec says `boundaryIssuePct` is "computed on-the-fly from `Chunk.BoundaryIssue` flags." The survey of the codebase showed no `BoundaryIssue` field on `DocChunk`; boundary-issue counts live in `RescrubResult` only. The plan handles this by **persisting `BoundaryIssuePct` to `LibraryVersionRecord`** at the end of every rescrub. `get_library_health` reads it back. This is a small deviation from the spec's wording but matches what the data model can actually support without a re-rescrub on every health query.

---

## Track C — Mutating Operations

### Task C1: Add `IPageRepository.DeleteAsync`

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/IPageRepository.cs`
- Modify: `SaddleRAG.Database/Repositories/PageRepository.cs`
- Test: `SaddleRAG.Tests/Mcp/MutationToolsTests.cs` (created in C7; for now, write a focused repo test in `SaddleRAG.Tests/Repositories/PageRepositoryTests.cs` if a similar one exists; otherwise inline-verify via the cascade test in C8)

- [ ] **Step 1: Add the interface method**

In `SaddleRAG.Core/Interfaces/IPageRepository.cs`, add:

```csharp
Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default);
```

Returns the deleted row count (so cascade reporting can show `pages: N`).

- [ ] **Step 2: Implement on `PageRepository`**

In `SaddleRAG.Database/Repositories/PageRepository.cs`, add (alongside existing methods, follow the file's region pattern):

```csharp
public async Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var filter = Builders<PageRecord>.Filter.And(
        Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
        Builders<PageRecord>.Filter.Eq(p => p.Version, version)
    );
    var result = await mContext.Pages.DeleteManyAsync(filter, ct);
    return result.DeletedCount;
}
```

- [ ] **Step 3: Build and confirm clean compile**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`
Expected: build succeeds with no errors and no new warnings.

- [ ] **Step 4: Commit**

```bash
git -C /e/GitHub/SaddleRAG add SaddleRAG.Core/Interfaces/IPageRepository.cs SaddleRAG.Database/Repositories/PageRepository.cs
git -C /e/GitHub/SaddleRAG commit -F /e/tmp/c1-msg.txt
```

`/e/tmp/c1-msg.txt`:
```
Add IPageRepository.DeleteAsync

Page-collection delete needed for delete_library / delete_version
cascade and submit_url_correction's clear-and-resubmit flow.
Returns deleted count for cascade reporting.
```

---

### Task C2: Verify and align Bm25Shard / ExcludedSymbols delete signatures

**Files:**
- Read: `SaddleRAG.Core/Interfaces/IBm25ShardRepository.cs`
- Read: `SaddleRAG.Core/Interfaces/IExcludedSymbolsRepository.cs`
- Possibly modify each if `DeleteAsync(libraryId, version)` doesn't exist or returns void instead of long

- [ ] **Step 1: Read both interfaces and check for existing `DeleteAsync(libraryId, version)`**

If both already have `Task DeleteAsync(string libraryId, string version, CancellationToken ct = default)`, change the return type to `Task<long>` (deleted count). If the method is missing, add it.

- [ ] **Step 2: Update implementations to return deleted count**

In `Bm25ShardRepository.cs` and `ExcludedSymbolsRepository.cs`, ensure `DeleteAsync` returns `result.DeletedCount` (Mongo `DeleteResult.DeletedCount`).

- [ ] **Step 3: Audit existing callers**

Run: `grep -rn "\.DeleteAsync" SaddleRAG.Mcp SaddleRAG.Ingestion SaddleRAG.Database`
For any caller that ignored the void return, no change needed (they can keep ignoring `Task<long>` — `await` discards the value). For any caller that explicitly bound the return, update the binding.

- [ ] **Step 4: Build clean**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```
Align Bm25ShardRepository / ExcludedSymbolsRepository DeleteAsync signatures

Both now return Task<long> (deleted count) so cascade reporting in
delete_library / delete_version can show per-collection counts.
```

---

### Task C3: Confirm `IChunkRepository.DeleteChunksAsync` and `ILibraryProfileRepository.DeleteAsync` and `ILibraryIndexRepository.DeleteAsync` return counts

**Files:**
- Same review pattern as C2, applied to chunk / profile / index repos.

- [ ] **Step 1: Read each interface**

If any still return `Task` instead of `Task<long>`, change to `Task<long>` and have the implementation return `result.DeletedCount`. The callers' `await foo.DeleteAsync(...)` continues to work.

- [ ] **Step 2: Build clean**

- [ ] **Step 3: Commit**

```
Align Chunk / Profile / Index DeleteAsync signatures to return count

Same change as previous commit, completing the cascade-reporting
groundwork for Track C delete tools.
```

---

### Task C4: Add `ILibraryRepository.DeleteVersionAsync`

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/ILibraryRepository.cs`
- Modify: `SaddleRAG.Database/Repositories/LibraryRepository.cs`

This method *only* deletes from the `LibraryVersions` collection and adjusts the parent `Library.CurrentVersion` if needed. The full per-version cascade lives in the MCP tool (Task C8) so the repo stays focused on a single collection.

- [ ] **Step 1: Add interface method**

```csharp
public sealed record DeleteVersionResult(long VersionsDeleted, bool LibraryRowDeleted, string? CurrentVersionRepointedTo);

Task<DeleteVersionResult> DeleteVersionAsync(string libraryId, string version, CancellationToken ct = default);
```

Define `DeleteVersionResult` in `SaddleRAG.Core/Models/DeleteVersionResult.cs` (new file).

- [ ] **Step 2: Create the result record**

Create `SaddleRAG.Core/Models/DeleteVersionResult.cs`:

```csharp
// DeleteVersionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Outcome of a single-version delete: how many version rows were
///     removed, whether the parent Library row was cascade-deleted
///     (because no versions remained), and the new currentVersion if
///     one had to be repointed.
/// </summary>
public sealed record DeleteVersionResult(long VersionsDeleted,
                                         bool LibraryRowDeleted,
                                         string? CurrentVersionRepointedTo);
```

- [ ] **Step 3: Implement in `LibraryRepository`**

In `LibraryRepository.cs`, add:

```csharp
public async Task<DeleteVersionResult> DeleteVersionAsync(string libraryId,
                                                          string version,
                                                          CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var versionFilter = Builders<LibraryVersionRecord>.Filter.And(
        Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId),
        Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
    );
    var versionsDeleted = (await mContext.LibraryVersions.DeleteManyAsync(versionFilter, ct)).DeletedCount;

    var remaining = await mContext.LibraryVersions
                                  .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                  .SortByDescending(v => v.ScrapedAt)
                                  .ToListAsync(ct);

    bool libraryRowDeleted = false;
    string? repointedTo = null;

    if (remaining.Count == 0)
    {
        var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
        var libDeleted = (await mContext.Libraries.DeleteOneAsync(libFilter, ct)).DeletedCount;
        libraryRowDeleted = libDeleted > 0;
    }
    else
    {
        var library = await GetLibraryAsync(libraryId, ct);
        if (library != null && library.CurrentVersion == version)
        {
            var newCurrent = remaining[0].Version;
            library.CurrentVersion = newCurrent;
            await UpsertLibraryAsync(library, ct);
            repointedTo = newCurrent;
        }
    }

    var result = new DeleteVersionResult(versionsDeleted, libraryRowDeleted, repointedTo);
    return result;
}
```

- [ ] **Step 4: Build clean**

- [ ] **Step 5: Commit**

```
Add ILibraryRepository.DeleteVersionAsync

Single-version cascade for delete_version. Removes the
LibraryVersions row, then either deletes the Library row (if no
versions remain) or repoints CurrentVersion to the next-most-recent
version. Returns a DeleteVersionResult so the MCP tool can report
LibraryRowAffected / CurrentVersionRepointedTo to the caller.
```

---

### Task C5: Add `ILibraryRepository.DeleteAsync`

**Files:** same as C4.

Full library-level delete. Calls `DeleteVersionAsync` for each version sequentially, then deletes the `Library` row (which `DeleteVersionAsync` may already have done if it's the last version, but the call is idempotent).

- [ ] **Step 1: Add interface method**

```csharp
Task<long> DeleteAsync(string libraryId, CancellationToken ct = default);
```

Returns the deleted version-row count. The MCP tool combines this with the per-collection cascade.

- [ ] **Step 2: Implement**

```csharp
public async Task<long> DeleteAsync(string libraryId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);

    var versions = await mContext.LibraryVersions
                                 .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                 .ToListAsync(ct);

    long total = 0;
    foreach (var v in versions)
    {
        var result = await DeleteVersionAsync(libraryId, v.Version, ct);
        total += result.VersionsDeleted;
    }

    var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
    await mContext.Libraries.DeleteOneAsync(libFilter, ct);

    return total;
}
```

- [ ] **Step 3: Build clean**

- [ ] **Step 4: Commit**

```
Add ILibraryRepository.DeleteAsync

Full-library cascade entrypoint. Iterates versions and calls
DeleteVersionAsync for each, then ensures the Library row is gone.
Caller (MCP tool) is responsible for cascading the per-version
collections (chunks, pages, indexes, etc.).
```

---

### Task C6: Add `ILibraryRepository.RenameAsync`

**Files:** same as C4. Plus all repos whose collections store `LibraryId`.

The rename is a multi-collection update: every collection that stores `LibraryId` needs the value rewritten. Sequencing matters less than completeness — we don't run inside a transaction (single-user system), so the rename is best-effort per collection. Returns a result with row counts per collection.

- [ ] **Step 1: Define result record**

Create `SaddleRAG.Core/Models/RenameLibraryResult.cs`:

```csharp
// RenameLibraryResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Per-collection update counts from a library rename.
/// </summary>
public sealed record RenameLibraryResult(long Libraries,
                                         long Versions,
                                         long Chunks,
                                         long Pages,
                                         long Profiles,
                                         long Indexes,
                                         long Bm25Shards,
                                         long ExcludedSymbols,
                                         long ScrapeJobs);
```

- [ ] **Step 2: Add interface method**

```csharp
public enum RenameLibraryOutcome { Renamed, Collision, NotFound }

public sealed record RenameLibraryResponse(RenameLibraryOutcome Outcome, RenameLibraryResult? Counts);

Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default);
```

Add the enum and response record near `DeleteVersionResult` in their own files (`RenameLibraryOutcome.cs`, `RenameLibraryResponse.cs`).

- [ ] **Step 3: Implement**

In `LibraryRepository.cs`:

```csharp
public async Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(oldId);
    ArgumentException.ThrowIfNullOrEmpty(newId);

    RenameLibraryResponse result;

    var existing = await GetLibraryAsync(oldId, ct);
    if (existing == null)
        result = new RenameLibraryResponse(RenameLibraryOutcome.NotFound, null);
    else
    {
        var collision = await GetLibraryAsync(newId, ct);
        if (collision != null)
            result = new RenameLibraryResponse(RenameLibraryOutcome.Collision, null);
        else
        {
            var counts = await ApplyRenameAsync(oldId, newId, ct);
            result = new RenameLibraryResponse(RenameLibraryOutcome.Renamed, counts);
        }
    }

    return result;
}

private async Task<RenameLibraryResult> ApplyRenameAsync(string oldId, string newId, CancellationToken ct)
{
    var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, oldId);
    var libUpdate = Builders<LibraryRecord>.Update.Set(l => l.Id, newId);
    var libRes = await mContext.Libraries.UpdateOneAsync(libFilter, libUpdate, cancellationToken: ct);

    var verFilter = Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, oldId);
    var verUpdate = Builders<LibraryVersionRecord>.Update.Set(v => v.LibraryId, newId);
    var verRes = await mContext.LibraryVersions.UpdateManyAsync(verFilter, verUpdate, cancellationToken: ct);

    var chunkFilter = Builders<DocChunk>.Filter.Eq(c => c.LibraryId, oldId);
    var chunkUpdate = Builders<DocChunk>.Update.Set(c => c.LibraryId, newId);
    var chunkRes = await mContext.Chunks.UpdateManyAsync(chunkFilter, chunkUpdate, cancellationToken: ct);

    var pageFilter = Builders<PageRecord>.Filter.Eq(p => p.LibraryId, oldId);
    var pageUpdate = Builders<PageRecord>.Update.Set(p => p.LibraryId, newId);
    var pageRes = await mContext.Pages.UpdateManyAsync(pageFilter, pageUpdate, cancellationToken: ct);

    var profileFilter = Builders<LibraryProfile>.Filter.Eq(p => p.LibraryId, oldId);
    var profileUpdate = Builders<LibraryProfile>.Update.Set(p => p.LibraryId, newId);
    var profileRes = await mContext.LibraryProfiles.UpdateManyAsync(profileFilter, profileUpdate, cancellationToken: ct);

    var indexFilter = Builders<LibraryIndex>.Filter.Eq(i => i.LibraryId, oldId);
    var indexUpdate = Builders<LibraryIndex>.Update.Set(i => i.LibraryId, newId);
    var indexRes = await mContext.LibraryIndexes.UpdateManyAsync(indexFilter, indexUpdate, cancellationToken: ct);

    var shardFilter = Builders<Bm25Shard>.Filter.Eq(s => s.LibraryId, oldId);
    var shardUpdate = Builders<Bm25Shard>.Update.Set(s => s.LibraryId, newId);
    var shardRes = await mContext.Bm25Shards.UpdateManyAsync(shardFilter, shardUpdate, cancellationToken: ct);

    var exFilter = Builders<ExcludedSymbol>.Filter.Eq(e => e.LibraryId, oldId);
    var exUpdate = Builders<ExcludedSymbol>.Update.Set(e => e.LibraryId, newId);
    var exRes = await mContext.ExcludedSymbols.UpdateManyAsync(exFilter, exUpdate, cancellationToken: ct);

    var jobFilter = Builders<ScrapeJobRecord>.Filter.Eq(j => j.Job.LibraryId, oldId);
    var jobUpdate = Builders<ScrapeJobRecord>.Update.Set(j => j.Job.LibraryId, newId);
    var jobRes = await mContext.ScrapeJobs.UpdateManyAsync(jobFilter, jobUpdate, cancellationToken: ct);

    var result = new RenameLibraryResult(libRes.ModifiedCount,
                                         verRes.ModifiedCount,
                                         chunkRes.ModifiedCount,
                                         pageRes.ModifiedCount,
                                         profileRes.ModifiedCount,
                                         indexRes.ModifiedCount,
                                         shardRes.ModifiedCount,
                                         exRes.ModifiedCount,
                                         jobRes.ModifiedCount);
    return result;
}
```

If `ScrapeJob.LibraryId` cannot be addressed via lambda (because `ScrapeJob` is a nested `init`-only record), fall back to a string filter: `Builders<ScrapeJobRecord>.Filter.Eq("Job.LibraryId", oldId)` and `Update.Set("Job.LibraryId", newId)`. Verify with a quick build.

- [ ] **Step 4: Build clean**

- [ ] **Step 5: Commit**

```
Add ILibraryRepository.RenameAsync

Cross-collection rename of LibraryId across Libraries,
LibraryVersions, Chunks, Pages, LibraryProfiles, LibraryIndexes,
Bm25Shards, ExcludedSymbols, ScrapeJobs. Pre-checks for collision
and missing source library. Returns per-collection update counts
for cascade-style reporting.
```

---

### Task C7: `rename_library` MCP tool

**Files:**
- Create: `SaddleRAG.Mcp/Tools/MutationTools.cs`
- Test: `SaddleRAG.Tests/Mcp/MutationToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `SaddleRAG.Tests/Mcp/MutationToolsTests.cs`:

```csharp
// MutationToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;
using NSubstitute;

namespace SaddleRAG.Tests.Mcp;

public sealed class MutationToolsTests
{
    [Fact]
    public async Task RenameLibrary_DryRun_ReportsCounts_WithoutWriting()
    {
        var (factory, libraryRepo) = MakeFactory();
        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                                     new RenameLibraryResult(1, 1, 100, 50, 1, 1, 1, 5, 3)));

        var json = await MutationTools.RenameLibrary(factory,
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Chunks\": 100", json);
        await libraryRepo.DidNotReceive().RenameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibrary_Apply_CallsRepoOnce()
    {
        var (factory, libraryRepo) = MakeFactory();
        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                                     new RenameLibraryResult(1, 1, 100, 50, 1, 1, 1, 5, 3)));

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
    public async Task RenameLibrary_Collision_ReportsAndDoesNotApply()
    {
        var (factory, libraryRepo) = MakeFactory();
        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Collision, null));

        var json = await MutationTools.RenameLibrary(factory,
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"Collision\"", json);
    }

    private static (RepositoryFactory factory, ILibraryRepository libraryRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        return (factory, libraryRepo);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test SaddleRAG.slnx --configuration Release --filter "MutationToolsTests"`
Expected: FAIL — `MutationTools` type does not exist.

- [ ] **Step 3: Create `MutationTools.cs` with `rename_library` only**

Create `SaddleRAG.Mcp/Tools/MutationTools.cs`:

```csharp
// MutationTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools that mutate library state — rename, delete library,
///     delete version. All default to dryRun=true so the calling LLM
///     can preview the cascade before committing.
/// </summary>
[McpServerToolType]
public static class MutationTools
{
    [McpServerTool(Name = "rename_library")]
    [Description("Rename a library across every collection that stores LibraryId. " +
                 "Defaults to dryRun=true — preview the per-collection update counts " +
                 "before passing dryRun=false to apply. Errors with Outcome=Collision " +
                 "if the new id already exists; never silently merges libraries."
                )]
    public static async Task<string> RenameLibrary(RepositoryFactory repositoryFactory,
                                                   [Description("Current library identifier")]
                                                   string library,
                                                   [Description("New library identifier")]
                                                   string newId,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(newId);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        string result;
        if (dryRun)
        {
            var counts = await PreviewRenameAsync(repositoryFactory, profile, library, newId, ct);
            var response = new
                               {
                                   DryRun = true,
                                   Outcome = counts.outcome.ToString(),
                                   WouldRename = counts.counts
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }
        else
        {
            var renameResult = await libraryRepo.RenameAsync(library, newId, ct);
            var response = new
                               {
                                   DryRun = false,
                                   Outcome = renameResult.Outcome.ToString(),
                                   Counts = renameResult.Counts
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    private static async Task<(RenameLibraryOutcome outcome, RenameLibraryResult? counts)> PreviewRenameAsync(
        RepositoryFactory factory, string? profile, string oldId, string newId, CancellationToken ct)
    {
        var libraryRepo = factory.GetLibraryRepository(profile);
        var existing = await libraryRepo.GetLibraryAsync(oldId, ct);
        var collision = await libraryRepo.GetLibraryAsync(newId, ct);

        (RenameLibraryOutcome outcome, RenameLibraryResult? counts) result;
        if (existing == null)
            result = (RenameLibraryOutcome.NotFound, null);
        else if (collision != null)
            result = (RenameLibraryOutcome.Collision, null);
        else
        {
            var chunkRepo = factory.GetChunkRepository(profile);
            var pageRepo = factory.GetPageRepository(profile);
            var counts = new RenameLibraryResult(
                Libraries: 1,
                Versions: existing.AllVersions.Count,
                Chunks: await chunkRepo.GetChunkCountAsync(oldId, version: existing.CurrentVersion, ct),
                Pages: await pageRepo.GetPageCountAsync(oldId, version: existing.CurrentVersion, ct),
                Profiles: 0,
                Indexes: 0,
                Bm25Shards: 0,
                ExcludedSymbols: 0,
                ScrapeJobs: 0);
            result = (RenameLibraryOutcome.Renamed, counts);
        }

        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
```

> **Implementer note:** `PreviewRenameAsync` returns approximate counts (it sums across the current version only and only hits Library/Chunk/Page repos). That's acceptable for dryRun — the question is "is this safe?", not "exact counts." If the approximation is too loose in practice, replace `PreviewRenameAsync` with a `LibraryRepository.PreviewRenameAsync` that issues `CountDocuments(filter)` against each affected collection (`Libraries`, `LibraryVersions`, `Chunks`, `Pages`, `LibraryProfiles`, `LibraryIndexes`, `Bm25Shards`, `ExcludedSymbols`, `ScrapeJobs`).

- [ ] **Step 4: Run the test, expect pass**

Run: `dotnet test SaddleRAG.slnx --configuration Release --filter "MutationToolsTests.RenameLibrary"`
Expected: PASS for all three rename tests.

- [ ] **Step 5: Commit**

```
Add rename_library MCP tool with dryRun=true default

Wraps ILibraryRepository.RenameAsync. dryRun=true returns
per-collection preview counts and the would-be Outcome (Renamed,
Collision, NotFound) without writing. dryRun=false applies the
rename in one repository call. Tests cover apply, dryRun, and
collision paths.
```

---

### Task C8: `delete_version` MCP tool

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/MutationTools.cs` — add `DeleteVersion` static method
- Test: append to `MutationToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `MutationToolsTests.cs`:

```csharp
[Fact]
public async Task DeleteVersion_DryRun_ReportsCascade_WithoutWriting()
{
    var (factory, libraryRepo) = MakeFactory();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var pageRepo = Substitute.For<IPageRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25Repo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

    factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
    factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
    factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
    factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
    factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
    factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);

    chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(123);
    pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(45);

    var json = await MutationTools.DeleteVersion(factory,
                                                 library: "foo",
                                                 version: "1.0",
                                                 dryRun: true,
                                                 profile: null,
                                                 ct: TestContext.Current.CancellationToken);

    Assert.Contains("\"DryRun\": true", json);
    Assert.Contains("\"Chunks\": 123", json);
    Assert.Contains("\"Pages\": 45", json);
    await chunkRepo.DidNotReceive().DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task DeleteVersion_Apply_CallsAllCollectionsThenLibraryDelete()
{
    var (factory, libraryRepo) = MakeFactory();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var pageRepo = Substitute.For<IPageRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25Repo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

    factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
    factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
    factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
    factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
    factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
    factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);

    libraryRepo.DeleteVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(new DeleteVersionResult(VersionsDeleted: 1, LibraryRowDeleted: false, CurrentVersionRepointedTo: "0.9"));

    var json = await MutationTools.DeleteVersion(factory,
                                                 library: "foo",
                                                 version: "1.0",
                                                 dryRun: false,
                                                 profile: null,
                                                 ct: TestContext.Current.CancellationToken);

    Assert.Contains("\"DryRun\": false", json);
    Assert.Contains("\"CurrentVersionRepointedTo\": \"0.9\"", json);
    await chunkRepo.Received(1).DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await pageRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await profileRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await indexRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await bm25Repo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await excludedRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await libraryRepo.Received(1).DeleteVersionAsync("foo", "1.0", Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run, expect failure**

- [ ] **Step 3: Implement `DeleteVersion` in `MutationTools.cs`**

Add to `MutationTools.cs`:

```csharp
[McpServerTool(Name = "delete_version")]
[Description("Hard-delete one (library, version): chunks, pages, profile, indexes, " +
             "bm25 shards, excluded symbols, and the LibraryVersions row. Cascade-deletes " +
             "the parent Library row if no other versions remain. ScrapeJobs are retained " +
             "for audit. Defaults to dryRun=true."
            )]
public static async Task<string> DeleteVersion(RepositoryFactory repositoryFactory,
                                               [Description("Library identifier")]
                                               string library,
                                               [Description("Version to delete")]
                                               string version,
                                               [Description("If true (default), preview without writing.")]
                                               bool dryRun = true,
                                               [Description("Optional database profile name")]
                                               string? profile = null,
                                               CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(repositoryFactory);
    ArgumentException.ThrowIfNullOrEmpty(library);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var chunkRepo = repositoryFactory.GetChunkRepository(profile);
    var pageRepo = repositoryFactory.GetPageRepository(profile);
    var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
    var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
    var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);
    var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
    var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

    string result;
    if (dryRun)
    {
        var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
        var pages = await pageRepo.GetPageCountAsync(library, version, ct);
        var lib = await libraryRepo.GetLibraryAsync(library, ct);
        bool wouldDeleteLibraryRow = lib != null && lib.AllVersions.Count == 1 && lib.AllVersions[0] == version;
        string? wouldRepointTo = lib != null && lib.CurrentVersion == version && lib.AllVersions.Count > 1
            ? lib.AllVersions.First(v => v != version)
            : null;

        var preview = new
                          {
                              DryRun = true,
                              WouldDelete = new
                                                {
                                                    Versions = new[] { version },
                                                    Chunks = chunks,
                                                    Pages = pages,
                                                    Profiles = 1,
                                                    Indexes = 1,
                                                    Bm25Shards = 1,
                                                    ExcludedSymbols = 1
                                                },
                              LibraryRowAffected = wouldDeleteLibraryRow,
                              CurrentVersionRepointedTo = wouldRepointTo,
                              ScrapeJobsRetained = "preserved for audit"
                          };
        result = JsonSerializer.Serialize(preview, smJsonOptions);
    }
    else
    {
        var chunks = await chunkRepo.DeleteChunksAsync(library, version, ct);
        var pages = await pageRepo.DeleteAsync(library, version, ct);
        var profiles = await profileRepo.DeleteAsync(library, version, ct);
        var indexes = await indexRepo.DeleteAsync(library, version, ct);
        var shards = await bm25Repo.DeleteAsync(library, version, ct);
        var excluded = await excludedRepo.DeleteAsync(library, version, ct);
        var versionResult = await libraryRepo.DeleteVersionAsync(library, version, ct);

        var response = new
                           {
                               DryRun = false,
                               Deleted = new
                                             {
                                                 Versions = versionResult.VersionsDeleted,
                                                 Chunks = chunks,
                                                 Pages = pages,
                                                 Profiles = profiles,
                                                 Indexes = indexes,
                                                 Bm25Shards = shards,
                                                 ExcludedSymbols = excluded
                                             },
                               versionResult.LibraryRowDeleted,
                               versionResult.CurrentVersionRepointedTo
                           };
        result = JsonSerializer.Serialize(response, smJsonOptions);
    }

    return result;
}
```

- [ ] **Step 4: Run tests, expect pass**

- [ ] **Step 5: Commit**

```
Add delete_version MCP tool with cascade and dryRun=true default

Cascades chunks, pages, profile, indexes, bm25 shards, excluded
symbols, then the LibraryVersions row (and the parent Library row
if last version). dryRun=true returns counts and would-be cascade
without writing. ScrapeJobs intentionally retained for audit.
```

---

### Task C9: `delete_library` MCP tool

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/MutationTools.cs` — add `DeleteLibrary`
- Test: append to `MutationToolsTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task DeleteLibrary_DryRun_AggregatesAcrossAllVersions_WithoutWriting()
{
    var (factory, libraryRepo) = MakeFactory();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var pageRepo = Substitute.For<IPageRepository>();
    factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
    factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);

    libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
               .Returns(new LibraryRecord
                            {
                                Id = "foo",
                                Name = "foo",
                                Hint = "h",
                                CurrentVersion = "2.0",
                                AllVersions = new List<string> { "1.0", "2.0" }
                            });
    chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50);
    chunkRepo.GetChunkCountAsync("foo", "2.0", Arg.Any<CancellationToken>()).Returns(100);
    pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(10);
    pageRepo.GetPageCountAsync("foo", "2.0", Arg.Any<CancellationToken>()).Returns(20);

    var json = await MutationTools.DeleteLibrary(factory,
                                                 library: "foo",
                                                 dryRun: true,
                                                 profile: null,
                                                 ct: TestContext.Current.CancellationToken);

    Assert.Contains("\"Versions\":", json);
    Assert.Contains("\"Chunks\": 150", json);
    Assert.Contains("\"Pages\": 30", json);
}

[Fact]
public async Task DeleteLibrary_Apply_DeletesEachVersionThenLibraryRow()
{
    var (factory, libraryRepo) = MakeFactory();
    var chunkRepo = Substitute.For<IChunkRepository>();
    var pageRepo = Substitute.For<IPageRepository>();
    var profileRepo = Substitute.For<ILibraryProfileRepository>();
    var indexRepo = Substitute.For<ILibraryIndexRepository>();
    var bm25Repo = Substitute.For<IBm25ShardRepository>();
    var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
    factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
    factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
    factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
    factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
    factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
    factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);

    libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
               .Returns(new LibraryRecord
                            {
                                Id = "foo",
                                Name = "foo",
                                Hint = "h",
                                CurrentVersion = "2.0",
                                AllVersions = new List<string> { "1.0", "2.0" }
                            });
    libraryRepo.DeleteAsync("foo", Arg.Any<CancellationToken>()).Returns(2);

    var json = await MutationTools.DeleteLibrary(factory,
                                                 library: "foo",
                                                 dryRun: false,
                                                 profile: null,
                                                 ct: TestContext.Current.CancellationToken);

    Assert.Contains("\"DryRun\": false", json);
    await chunkRepo.Received().DeleteChunksAsync("foo", Arg.Any<string>(), Arg.Any<CancellationToken>());
    await libraryRepo.Received(1).DeleteAsync("foo", Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run, expect fail**

- [ ] **Step 3: Implement `DeleteLibrary`**

Add to `MutationTools.cs`:

```csharp
[McpServerTool(Name = "delete_library")]
[Description("Hard-delete an entire library across every collection except ScrapeJobs " +
             "(retained for audit). Cascades through every version. Defaults to " +
             "dryRun=true so the calling LLM can preview the cascade before applying."
            )]
public static async Task<string> DeleteLibrary(RepositoryFactory repositoryFactory,
                                               [Description("Library identifier")]
                                               string library,
                                               [Description("If true (default), preview without writing.")]
                                               bool dryRun = true,
                                               [Description("Optional database profile name")]
                                               string? profile = null,
                                               CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(repositoryFactory);
    ArgumentException.ThrowIfNullOrEmpty(library);

    var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
    var chunkRepo = repositoryFactory.GetChunkRepository(profile);
    var pageRepo = repositoryFactory.GetPageRepository(profile);
    var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
    var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
    var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);
    var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);

    var lib = await libraryRepo.GetLibraryAsync(library, ct);
    string result;
    if (lib == null)
    {
        var nf = new { Status = "NotFound", Library = library };
        result = JsonSerializer.Serialize(nf, smJsonOptions);
    }
    else if (dryRun)
    {
        long totalChunks = 0;
        long totalPages = 0;
        foreach (var v in lib.AllVersions)
        {
            totalChunks += await chunkRepo.GetChunkCountAsync(library, v, ct);
            totalPages += await pageRepo.GetPageCountAsync(library, v, ct);
        }

        var preview = new
                          {
                              DryRun = true,
                              WouldDelete = new
                                                {
                                                    Library = 1,
                                                    Versions = lib.AllVersions.ToArray(),
                                                    Chunks = totalChunks,
                                                    Pages = totalPages,
                                                    Profiles = lib.AllVersions.Count,
                                                    Indexes = lib.AllVersions.Count,
                                                    Bm25Shards = lib.AllVersions.Count,
                                                    ExcludedSymbols = lib.AllVersions.Count
                                                },
                              ScrapeJobsRetained = "preserved for audit"
                          };
        result = JsonSerializer.Serialize(preview, smJsonOptions);
    }
    else
    {
        long chunks = 0, pages = 0, profiles = 0, indexes = 0, shards = 0, excluded = 0;
        foreach (var v in lib.AllVersions)
        {
            chunks += await chunkRepo.DeleteChunksAsync(library, v, ct);
            pages += await pageRepo.DeleteAsync(library, v, ct);
            profiles += await profileRepo.DeleteAsync(library, v, ct);
            indexes += await indexRepo.DeleteAsync(library, v, ct);
            shards += await bm25Repo.DeleteAsync(library, v, ct);
            excluded += await excludedRepo.DeleteAsync(library, v, ct);
        }

        var versionsDeleted = await libraryRepo.DeleteAsync(library, ct);

        var response = new
                           {
                               DryRun = false,
                               Deleted = new
                                             {
                                                 Library = 1,
                                                 Versions = versionsDeleted,
                                                 Chunks = chunks,
                                                 Pages = pages,
                                                 Profiles = profiles,
                                                 Indexes = indexes,
                                                 Bm25Shards = shards,
                                                 ExcludedSymbols = excluded
                                             }
                           };
        result = JsonSerializer.Serialize(response, smJsonOptions);
    }

    return result;
}
```

- [ ] **Step 4: Run tests, expect pass**

- [ ] **Step 5: Commit**

```
Add delete_library MCP tool with full cascade and dryRun=true default

Iterates every version, cascades through chunks, pages, profile,
indexes, bm25 shards, excluded symbols per version, then deletes
the Library row. ScrapeJobs intentionally retained. dryRun
returns aggregate counts across all versions without writing.
```

---

## Track F — Job Cancellation

### Task F1: Add `Cancelled = 4` to `ScrapeJobStatus`

**Files:**
- Modify: `SaddleRAG.Core/Enums/ScrapeJobStatus.cs`

- [ ] **Step 1: Add the enum member**

Locate `ScrapeJobStatus` and add `Cancelled = 4` as the next value.

- [ ] **Step 2: Build clean**

- [ ] **Step 3: Commit**

```
Add ScrapeJobStatus.Cancelled enum value

Foundation for Track F cancel_scrape support.
```

---

### Task F2: Add `LastProgressAt` and `CancelledAt` to `ScrapeJobRecord`

**Files:**
- Modify: `SaddleRAG.Core/Models/ScrapeJobRecord.cs`

- [ ] **Step 1: Add fields**

```csharp
public DateTime? LastProgressAt { get; set; }
public DateTime? CancelledAt { get; set; }
```

- [ ] **Step 2: Build clean**

- [ ] **Step 3: Commit**

```
Add ScrapeJobRecord.LastProgressAt and CancelledAt

LastProgressAt is updated whenever any pipeline counter increments
(used by get_dashboard_index Stale-flag detection). CancelledAt is
set when transitioning to ScrapeJobStatus.Cancelled.
```

---

### Task F3: Add CTS registry to `ScrapeJobRunner` and `CancelAsync`

**Files:**
- Modify: `SaddleRAG.Ingestion/ScrapeJobRunner.cs`

- [ ] **Step 1: Add CTS registry field**

Near the existing `smJobLocks` static, add:

```csharp
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> mActiveJobCts = new();
```

- [ ] **Step 2: Register CTS at job start, dispose in finally**

In `RunJobAsync`, wrap the pipeline-execution block with CTS registration:

```csharp
var cts = CancellationTokenSource.CreateLinkedTokenSource(mAppStoppingToken);
mActiveJobCts[jobRecord.Id] = cts;
try
{
    // existing pipeline execution code, passing cts.Token instead of the previous token source
}
finally
{
    mActiveJobCts.TryRemove(jobRecord.Id, out _);
    cts.Dispose();
}
```

If the existing code creates its own CTS internally, replace that creation with the new registered `cts`. Locate the orchestrator call and update its token argument accordingly.

- [ ] **Step 3: Add `CancelAsync` method (mark `virtual` so NSubstitute can mock it in tests)**

```csharp
public virtual async Task<CancelScrapeOutcome> CancelAsync(string jobId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(jobId);

    var jobRepo = mRepositoryFactory.GetScrapeJobRepository(profile: null);
    var record = await jobRepo.GetAsync(jobId, ct);

    CancelScrapeOutcome result;
    if (record == null)
        result = CancelScrapeOutcome.NotFound;
    else if (record.Status is ScrapeJobStatus.Completed or ScrapeJobStatus.Failed or ScrapeJobStatus.Cancelled)
        result = CancelScrapeOutcome.AlreadyTerminal;
    else
    {
        if (mActiveJobCts.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
            result = CancelScrapeOutcome.Signalled;
        }
        else
        {
            result = CancelScrapeOutcome.OrphanCleanedUp;
        }

        record.Status = ScrapeJobStatus.Cancelled;
        record.PipelineState = nameof(ScrapeJobStatus.Cancelled);
        record.CancelledAt = DateTime.UtcNow;
        record.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(record, ct);
    }

    return result;
}

public enum CancelScrapeOutcome
{
    Signalled,
    OrphanCleanedUp,
    AlreadyTerminal,
    NotFound
}
```

If `CancelScrapeOutcome` should live in `SaddleRAG.Core/Enums/` instead, move it there. Either location is fine; keep it findable from `cancel_scrape` and the runner.

- [ ] **Step 4: Build clean**

- [ ] **Step 5: Commit**

```
Add CTS registry and CancelAsync to ScrapeJobRunner

Each running job registers its CancellationTokenSource in a
ConcurrentDictionary keyed by jobId; removed in a finally block on
completion. CancelAsync signals an active CTS or, for orphans
(process restarted while job was Running), updates the DB row
directly. Returns a CancelScrapeOutcome the MCP tool can map to a
user-facing status.
```

---

### Task F4: Update progress callback to set `LastProgressAt`

**Files:**
- Modify: `SaddleRAG.Ingestion/ScrapeJobRunner.cs`

- [ ] **Step 1: Detect counter changes in the progress callback**

In the progress callback inside `RunJobAsync` that copies counters from the orchestrator's progress record:

```csharp
mOrchestrator.IngestAsync(..., updatedRecord =>
{
    bool counterIncreased =
        updatedRecord.PagesQueued != jobRecord.PagesQueued ||
        updatedRecord.PagesFetched != jobRecord.PagesFetched ||
        updatedRecord.PagesClassified != jobRecord.PagesClassified ||
        updatedRecord.ChunksGenerated != jobRecord.ChunksGenerated ||
        updatedRecord.ChunksEmbedded != jobRecord.ChunksEmbedded ||
        updatedRecord.ChunksCompleted != jobRecord.ChunksCompleted ||
        updatedRecord.PagesCompleted != jobRecord.PagesCompleted;

    jobRecord.PipelineState = updatedRecord.PipelineState;
    jobRecord.PagesQueued = updatedRecord.PagesQueued;
    jobRecord.PagesFetched = updatedRecord.PagesFetched;
    jobRecord.PagesClassified = updatedRecord.PagesClassified;
    jobRecord.ChunksGenerated = updatedRecord.ChunksGenerated;
    jobRecord.ChunksEmbedded = updatedRecord.ChunksEmbedded;
    jobRecord.ChunksCompleted = updatedRecord.ChunksCompleted;
    jobRecord.PagesCompleted = updatedRecord.PagesCompleted;

    if (counterIncreased)
        jobRecord.LastProgressAt = DateTime.UtcNow;

    mJobRepository.UpsertAsync(jobRecord).GetAwaiter().GetResult();
});
```

- [ ] **Step 2: Build clean**

- [ ] **Step 3: Commit**

```
Update ScrapeJobRunner to set LastProgressAt on counter increments

Forward motion in any of the seven progress counters refreshes
LastProgressAt. get_dashboard_index will use this to flag
Running jobs whose LastProgressAt is older than 4 hours.
```

---

### Task F5: `cancel_scrape` MCP tool

**Files:**
- Create: `SaddleRAG.Mcp/Tools/CancellationTools.cs`
- Test: `SaddleRAG.Tests/Mcp/CancellationToolsTests.cs`

- [ ] **Step 1: Write failing test**

Create `SaddleRAG.Tests/Mcp/CancellationToolsTests.cs`:

```csharp
using SaddleRAG.Core.Enums;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Tools;
using NSubstitute;

namespace SaddleRAG.Tests.Mcp;

public sealed class CancellationToolsTests
{
    [Fact]
    public async Task CancelScrape_Signalled_ReturnsRunningStatus()
    {
        // Pass nulls for the runner's constructor args — only CancelAsync is exercised here.
// If ScrapeJobRunner's constructor refuses nulls, capture the actual arg list from
// its definition and pass minimal Substitute.For<>() doubles for each.
var runner = Substitute.ForPartsOf<ScrapeJobRunner>(new object?[] { null, null, null, null });
        runner.CancelAsync("abc", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.Signalled);

        var json = await CancellationTools.CancelScrape(runner, jobId: "abc",
                                                        ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"Signalled\"", json);
    }

    [Fact]
    public async Task CancelScrape_NotFound_ReturnsNotFoundStatus()
    {
        var runner = Substitute.ForPartsOf<ScrapeJobRunner>(new object?[] { null, null, null, null });
        runner.CancelAsync("missing", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.NotFound);

        var json = await CancellationTools.CancelScrape(runner, jobId: "missing",
                                                        ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"NotFound\"", json);
    }
}
```

> **Note:** the test relies on `CancelAsync` being `virtual` (set in F3 step 3). If `Substitute.ForPartsOf<ScrapeJobRunner>(...)` proves brittle (e.g. constructor side-effects), the cleanest alternative is to extract `CancelAsync` onto an `IScrapeJobCanceller` interface implemented by the runner and depend on the interface from `CancellationTools`. Defer that refactor unless the partial-substitute approach actually fails.

- [ ] **Step 2: Run, expect fail**

- [ ] **Step 3: Create `CancellationTools.cs`**

```csharp
// CancellationTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Ingestion;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

[McpServerToolType]
public static class CancellationTools
{
    [McpServerTool(Name = "cancel_scrape")]
    [Description("Cancel a running scrape job. Signals the pipeline cancellation token " +
                 "for active jobs, or marks the DB row Cancelled directly for orphaned " +
                 "jobs (process restarted while job was Running). No-op for jobs already " +
                 "Completed/Failed/Cancelled. Partial results are kept — call delete_version " +
                 "or submit_url_correction afterward to clear them."
                )]
    public static async Task<string> CancelScrape(ScrapeJobRunner runner,
                                                  [Description("Job id from list_scrape_jobs or get_scrape_status")]
                                                  string jobId,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var outcome = await runner.CancelAsync(jobId, ct);

        var response = new
                           {
                               JobId = jobId,
                               Outcome = outcome.ToString(),
                               Message = outcome switch
                                             {
                                                 CancelScrapeOutcome.Signalled => "Pipeline cancellation signalled. Job will transition to Cancelled.",
                                                 CancelScrapeOutcome.OrphanCleanedUp => "Job had no active runner; DB row marked Cancelled directly.",
                                                 CancelScrapeOutcome.AlreadyTerminal => "Job is already Completed, Failed, or Cancelled. No action taken.",
                                                 CancelScrapeOutcome.NotFound => "No scrape job found with that id.",
                                                 _ => "Unknown outcome."
                                             }
                           };
        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
```

- [ ] **Step 4: Run tests, expect pass**

- [ ] **Step 5: Commit**

```
Add cancel_scrape MCP tool

Wraps ScrapeJobRunner.CancelAsync. Returns a structured Outcome
(Signalled, OrphanCleanedUp, AlreadyTerminal, NotFound) so the
calling LLM can decide whether to follow up with delete_version
to clear partial results.
```

---

### Task F6: Add `IngestStatus.InProgress` and surface in `start_ingest`

**Files:**
- Modify: `SaddleRAG.Core/Enums/IngestStatus.cs` — add `InProgress`
- Modify: `SaddleRAG.Mcp/Tools/IngestTools.cs` — detect active job, return `IN_PROGRESS`
- Modify: `SaddleRAG.Core/Interfaces/IScrapeJobRepository.cs` — add `GetActiveJobAsync`
- Modify: `SaddleRAG.Database/Repositories/ScrapeJobRepository.cs` — implement `GetActiveJobAsync`

- [ ] **Step 1: Add enum value**

In `IngestStatus.cs`, append `InProgress`. (Keep `UrlSuspect` for Track E.)

- [ ] **Step 2: Add `GetActiveJobAsync` to scrape job repo**

Interface:
```csharp
Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId, string version, CancellationToken ct = default);
```

Implementation:
```csharp
public async Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId, string version, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var filter = Builders<ScrapeJobRecord>.Filter.And(
        Builders<ScrapeJobRecord>.Filter.Eq("Job.LibraryId", libraryId),
        Builders<ScrapeJobRecord>.Filter.Eq("Job.Version", version),
        Builders<ScrapeJobRecord>.Filter.In(j => j.Status,
            new[] { ScrapeJobStatus.Queued, ScrapeJobStatus.Running })
    );
    var result = await mContext.ScrapeJobs.Find(filter)
                               .SortByDescending(j => j.CreatedAt)
                               .FirstOrDefaultAsync(ct);
    return result;
}
```

- [ ] **Step 3: Wire `IN_PROGRESS` into `IngestTools.ResolveStatus`**

Modify `StartIngest` to query the active-job repo before constructing the response:

```csharp
var activeJob = await scrapeJobRepo.GetActiveJobAsync(library, version, ct);
if (activeJob != null)
{
    var inProgressResponse = MakeInProgress(library, version, url, activeJob.Id);
    return JsonSerializer.Serialize(inProgressResponse, smJsonOptions);
}
```

Add `MakeInProgress`:

```csharp
private static IngestStatusResponse MakeInProgress(string library, string version, string url, string jobId) =>
    new()
        {
            Status = IngestStatus.InProgress,
            LibraryId = library,
            Version = version,
            Url = url,
            NextTool = "get_scrape_status",
            Message = $"Scrape job {jobId} is already running. Poll get_scrape_status, or call cancel_scrape to abort.",
            NextToolArgs = new Dictionary<string, string>
                               {
                                   ["jobId"] = jobId
                               }
        };
```

- [ ] **Step 4: Build, run start_ingest tests if any**

Run: `dotnet test SaddleRAG.slnx --configuration Release --filter "IngestTools"`
Expected: PASS (if tests existed). New behavior is covered by manual MCP exercise; an integration test follows in Task F7.

- [ ] **Step 5: Commit**

```
Add IN_PROGRESS state to start_ingest

When a scrape job is Queued or Running for (library, version),
start_ingest returns Status=InProgress with the jobId and
get_scrape_status as nextTool. Prevents duplicate work and gives
the LLM a clear path to either poll or call cancel_scrape.
```

---

### Task F7: Tests for cancellation end-to-end

**Files:**
- Append to `CancellationToolsTests.cs`: cover orphan vs active. Add a `ScrapeJobRunner` integration test (or, if the runner is hard to mock, an `IScrapeJobCanceller` interface test) covering the two paths and the AlreadyTerminal branch.

- [ ] **Step 1: Add tests for OrphanCleanedUp and AlreadyTerminal**

```csharp
[Fact]
public async Task CancelScrape_OrphanCleanedUp_UpdatesDbWithoutSignal()
{
    var runner = Substitute.ForPartsOf<ScrapeJobRunner>(new object?[] { null, null, null, null });
    runner.CancelAsync("orphan", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.OrphanCleanedUp);

    var json = await CancellationTools.CancelScrape(runner, jobId: "orphan",
                                                    ct: TestContext.Current.CancellationToken);
    Assert.Contains("\"Outcome\": \"OrphanCleanedUp\"", json);
}

[Fact]
public async Task CancelScrape_AlreadyTerminal_NoOp()
{
    var runner = Substitute.ForPartsOf<ScrapeJobRunner>(new object?[] { null, null, null, null });
    runner.CancelAsync("done", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.AlreadyTerminal);

    var json = await CancellationTools.CancelScrape(runner, jobId: "done",
                                                    ct: TestContext.Current.CancellationToken);
    Assert.Contains("\"Outcome\": \"AlreadyTerminal\"", json);
}
```

- [ ] **Step 2: Run all tests, expect pass**

- [ ] **Step 3: Commit**

```
Add CancellationToolsTests covering orphan and terminal paths

Round out cancel_scrape coverage for the four CancelScrapeOutcome
values.
```

---

## Track G — Surface Consolidation

### Task G1: Add `resume` flag to `scrape_docs`, fold `continue_scrape` behavior

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` — add resume, change url to optional, factor existing continue_scrape lookup logic into shared helper
- Test: extend any existing scrape_docs test to cover resume; if no test exists, create `SaddleRAG.Tests/Mcp/ScrapeDocsToolsTests.cs`

- [ ] **Step 1: Add resume flag and rewire**

Modify `ScrapeDocs` signature: change `url` from required to `string? url = null`, add `bool resume = false` parameter near `force`.

Inside the method, before `existingVersion` lookup:

```csharp
ScrapeJob? jobToQueue = null;

if (resume)
{
    var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
    var recent = await jobRepo.ListRecentAsync(limit: 100, ct);
    var previous = recent.Where(j => j.Job.LibraryId == libraryId && j.Job.Version == version)
                         .OrderByDescending(j => j.CreatedAt)
                         .FirstOrDefault();

    if (previous == null)
    {
        var noprior = new
                          {
                              Status = "NoPriorJob",
                              Message = "resume=true but no previous scrape job exists for " +
                                        $"{libraryId} v{version}. Pass url to start a fresh scrape."
                          };
        return JsonSerializer.Serialize(noprior, new JsonSerializerOptions { WriteIndented = true });
    }

    // Build a job using previous values, with caller overrides
    jobToQueue = new ScrapeJob
                     {
                         RootUrl = url ?? previous.Job.RootUrl,
                         LibraryId = libraryId,
                         Version = version,
                         LibraryHint = hint ?? previous.Job.LibraryHint,
                         AllowedUrlPatterns = previous.Job.AllowedUrlPatterns,
                         ExcludedUrlPatterns = previous.Job.ExcludedUrlPatterns,
                         MaxPages = maxPages,
                         FetchDelayMs = fetchDelayMs
                     };
}
else
{
    if (string.IsNullOrEmpty(url))
        throw new ArgumentException("url is required when resume=false");
}
```

After the cache check (which is unchanged), if `jobToQueue == null`, use the existing `ScrapeJobFactory.CreateFromUrl(...)` path. Otherwise queue `jobToQueue`.

- [ ] **Step 2: Build clean, run any existing scrape tests**

- [ ] **Step 3: Commit**

```
Add resume flag to scrape_docs

When resume=true, scrape_docs reads the most recent ScrapeJob for
(libraryId, version) and reuses its RootUrl and pattern config.
url, hint, allowedUrlPatterns, excludedUrlPatterns become optional
overrides. Returns Status=NoPriorJob if no previous job exists.
This subsumes continue_scrape's behavior; continue_scrape is
removed in the next commit.
```

---

### Task G2: Remove `continue_scrape`

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` — delete `ContinueScrape` static method and its constants
- Update any callers (notably the spec mentions Track E refusal — that moves to scrape_docs(resume=true))

- [ ] **Step 1: Delete the method**

In `ScrapeDocsTools.cs`, remove the `ContinueScrape` method, the `[McpServerTool(Name = "continue_scrape")]` attribute, and the `StartNewScrapeMessage` constant if it's no longer referenced.

- [ ] **Step 2: Build clean (no other callers should fail)**

- [ ] **Step 3: Commit**

```
Remove continue_scrape MCP tool

continue_scrape's behavior is now scrape_docs(resume=true). One
less tool on the surface and the LLM no longer has to choose
between two near-identical entrypoints.
```

---

### Task G3: Replace 4 list_* tools with `list_symbols`

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/LibraryTools.cs` — remove ListClasses, ListEnums, ListFunctions, ListParameters; add ListSymbols
- Modify: `SaddleRAG.Core/Interfaces/IChunkRepository.cs` — add a `GetAllSymbolsAsync` method that returns `IReadOnlyList<Symbol>` for `kind=null` callers
- Modify: `SaddleRAG.Database/Repositories/ChunkRepository.cs` — implement
- Test: `SaddleRAG.Tests/Mcp/ListSymbolsToolTests.cs`

- [ ] **Step 1: Add `GetAllSymbolsAsync` to chunk repo**

Interface:
```csharp
Task<IReadOnlyList<Symbol>> GetAllSymbolsAsync(string libraryId, string version, string? filter = null, CancellationToken ct = default);
```

Implementation: aggregate all symbols across chunks. The simplest implementation reads chunks and flattens their `Symbols` array:

```csharp
public async Task<IReadOnlyList<Symbol>> GetAllSymbolsAsync(string libraryId, string version, string? filter = null, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var chunks = await GetChunksAsync(libraryId, version, ct);
    var seen = new HashSet<(string Name, SymbolKind Kind)>();
    var symbols = new List<Symbol>();
    foreach (var chunk in chunks)
    {
        foreach (var s in chunk.Symbols)
        {
            if (!string.IsNullOrEmpty(filter) && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = (s.Name, s.Kind);
            if (seen.Add(key))
                symbols.Add(s);
        }
    }
    var result = (IReadOnlyList<Symbol>) symbols;
    return result;
}
```

(If you want to avoid loading every chunk in memory, use `Builders<DocChunk>.Projection.Include` to project only `Symbols` and run a Mongo aggregation. The above is fine for first cut.)

- [ ] **Step 2: Write failing test**

`SaddleRAG.Tests/Mcp/ListSymbolsToolTests.cs`:

```csharp
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;
using NSubstitute;

namespace SaddleRAG.Tests.Mcp;

public sealed class ListSymbolsToolTests
{
    [Fact]
    public async Task ListSymbols_ClassKind_ReturnsClassesOnly()
    {
        var (factory, libraryRepo, chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord { Id = "foo", Name = "f", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } });
        chunkRepo.GetSymbolsAsync("foo", "1.0", SymbolKind.Class, null, Arg.Any<CancellationToken>())
                 .Returns(new[] { "ClassA", "ClassB" });

        var json = await LibraryTools.ListSymbols(factory, library: "foo", kind: "class",
                                                  ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"ClassA\"", json);
        Assert.Contains("\"kind\": \"class\"", json);
    }

    [Fact]
    public async Task ListSymbols_NullKind_ReturnsAllKindsTagged()
    {
        var (factory, libraryRepo, chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord { Id = "foo", Name = "f", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } });
        chunkRepo.GetAllSymbolsAsync("foo", "1.0", null, Arg.Any<CancellationToken>())
                 .Returns(new[]
                              {
                                  new Symbol { Name = "ClassA", Kind = SymbolKind.Class },
                                  new Symbol { Name = "FuncB", Kind = SymbolKind.Function }
                              });

        var json = await LibraryTools.ListSymbols(factory, library: "foo", kind: null,
                                                  ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"ClassA\"", json);
        Assert.Contains("\"FuncB\"", json);
        Assert.Contains("\"kind\": \"class\"", json);
        Assert.Contains("\"kind\": \"function\"", json);
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
```

- [ ] **Step 3: Add `ListSymbols` to `LibraryTools.cs`**

```csharp
[McpServerTool(Name = "list_symbols")]
[Description("List documented symbols for a library, optionally filtered by kind. " +
             "kind=class|enum|function|parameter, or omit for all kinds. " +
             "Returns [{name, kind}] so callers can render heterogeneous results."
            )]
public static async Task<string> ListSymbols(RepositoryFactory repositoryFactory,
                                             [Description("Library identifier")]
                                             string library,
                                             [Description("Symbol kind filter: 'class', 'enum', 'function', 'parameter', or null for all")]
                                             string? kind = null,
                                             [Description("Optional partial name filter (case-insensitive)")]
                                             string? filter = null,
                                             [Description("Specific version — defaults to current")]
                                             string? version = null,
                                             [Description("Optional database profile name")]
                                             string? profile = null,
                                             CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(repositoryFactory);
    ArgumentException.ThrowIfNullOrEmpty(library);

    var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
    var chunkRepo = repositoryFactory.GetChunkRepository(profile);
    var resolvedVersion = await ResolveVersionAsync(libraryRepo, library, version, ct);

    string result;
    if (resolvedVersion == null)
    {
        var nf = new { Error = $"Library '{library}' not found." };
        result = JsonSerializer.Serialize(nf, smJsonOptions);
    }
    else
    {
        var entries = new List<object>();
        if (kind == null)
        {
            var all = await chunkRepo.GetAllSymbolsAsync(library, resolvedVersion, filter, ct);
            entries.AddRange(all.Select(s => (object) new { name = s.Name, kind = s.Kind.ToString().ToLowerInvariant() }));
        }
        else
        {
            var parsed = ParseKind(kind);
            var names = await chunkRepo.GetSymbolsAsync(library, resolvedVersion, parsed, filter, ct);
            entries.AddRange(names.Select(n => (object) new { name = n, kind = parsed.ToString().ToLowerInvariant() }));
        }
        result = JsonSerializer.Serialize(entries, smJsonOptions);
    }
    return result;
}

private static SymbolKind ParseKind(string raw) => raw.ToLowerInvariant() switch
{
    "class" => SymbolKind.Class,
    "enum" => SymbolKind.Enum,
    "function" => SymbolKind.Function,
    "parameter" => SymbolKind.Parameter,
    _ => throw new ArgumentException($"Unknown kind '{raw}'. Expected: class, enum, function, parameter.")
};
```

- [ ] **Step 4: Remove the four old tool methods (`ListClasses`, `ListEnums`, `ListFunctions`, `ListParameters`) and the `ListSymbolsByKindAsync` helper if no longer needed.**

- [ ] **Step 5: Run tests, expect pass**

- [ ] **Step 6: Commit**

```
Replace list_classes/enums/functions/parameters with list_symbols

One parameter-discriminated tool replaces four near-duplicates.
kind=null returns all kinds. Each entry carries {name, kind} so
mixed-result rendering is unambiguous.
```

---

### Task G4: Remove `scrape_library` (Track D start)

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/IngestionTools.cs` — delete `ScrapeLibrary` method
- Modify: `SaddleRAG.Mcp/Tools/IngestTools.cs` — `MakeReadyToScrape` points at `scrape_docs`

- [ ] **Step 1: Remove `ScrapeLibrary`**

Delete the `[McpServerTool(Name = "scrape_library")]` method block in `IngestionTools.cs`. Keep `dryrun_scrape`, `get_scrape_status`, `list_scrape_jobs`, `reload_profile` untouched.

- [ ] **Step 2: Update `start_ingest`'s ready-to-scrape response**

In `MakeReadyToScrape`, change `NextTool = "scrape_library"` to `NextTool = "scrape_docs"` and update `NextToolArgs` keys: `rootUrl` → `url`, `libraryId` → `libraryId`, `version` → `version`. (`scrape_docs` uses `url`/`libraryId`/`version`, not `rootUrl`.)

- [ ] **Step 3: Build clean**

- [ ] **Step 4: Commit**

```
Remove scrape_library; start_ingest READY_TO_SCRAPE points at scrape_docs

scrape_docs gained allowedUrlPatterns/excludedUrlPatterns (next
commit) and resume support (Track G), so it covers everything
scrape_library did. One less tool, and the LLM no longer has to
choose between scrape_library and scrape_docs from descriptions
alone.
```

---

### Task G5: Add `allowedUrlPatterns` / `excludedUrlPatterns` to `scrape_docs`

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs`

- [ ] **Step 1: Add params**

In `ScrapeDocs`, after `force`:

```csharp
[Description("Optional URL patterns (regex) to allow. Defaults to the rootUrl host when omitted.")]
string[]? allowedUrlPatterns = null,
[Description("Optional URL patterns (regex) to exclude.")]
string[]? excludedUrlPatterns = null,
```

When building the job (the `ScrapeJobFactory.CreateFromUrl` call path), pass these through. If `ScrapeJobFactory.CreateFromUrl` doesn't take patterns, build the `ScrapeJob` directly when patterns are non-null:

```csharp
ScrapeJob job;
if (allowedUrlPatterns != null || excludedUrlPatterns != null)
{
    var allowed = allowedUrlPatterns ?? new[] { new Uri(url!).Host };
    job = new ScrapeJob
              {
                  RootUrl = url!,
                  LibraryId = libraryId,
                  Version = version,
                  LibraryHint = hint ?? "",
                  AllowedUrlPatterns = allowed,
                  ExcludedUrlPatterns = excludedUrlPatterns ?? Array.Empty<string>(),
                  MaxPages = maxPages,
                  FetchDelayMs = fetchDelayMs,
                  ForceClean = force
              };
}
else
{
    job = ScrapeJobFactory.CreateFromUrl(url!, libraryId, version, hint, maxPages, fetchDelayMs, forceClean: force);
}
```

- [ ] **Step 2: Update description**

Replace the existing description string with one that explicitly contrasts auto vs explicit pattern modes:

```
"Scrape documentation from a URL. Cache-aware: returns AlreadyCached " +
"unless force=true. Pass allowedUrlPatterns / excludedUrlPatterns only " +
"if the auto-derived host filter is too narrow or too broad. Use this " +
"for both ad-hoc URLs and post-recon scrapes — there is no separate " +
"scrape_library tool. resume=true reuses the most recent ScrapeJob's " +
"rootUrl and patterns when url is omitted."
```

- [ ] **Step 3: Build clean**

- [ ] **Step 4: Commit**

```
Add allowedUrlPatterns and excludedUrlPatterns to scrape_docs

scrape_docs now subsumes scrape_library's manual-pattern path. When
patterns are omitted, behavior is unchanged (auto-derived from
rootUrl host). Description sharpened to explicitly tell the calling
LLM when each mode applies.
```

---

## Track B — `get_library_health`

### Task B1: Add `BoundaryIssuePct` to `LibraryVersionRecord`

**Files:**
- Modify: `SaddleRAG.Core/Models/LibraryVersionRecord.cs`

- [ ] **Step 1: Add field**

```csharp
public double BoundaryIssuePct { get; set; }   // 0.0–100.0; updated by RescrubService
```

- [ ] **Step 2: Build, commit**

```
Add LibraryVersionRecord.BoundaryIssuePct

Persisted summary of the most recent rescrub's boundary-issue rate.
get_library_health surfaces this without recomputing chunks.
```

---

### Task B2: Update `RescrubService` to persist `BoundaryIssuePct`

**Files:**
- Modify: `SaddleRAG.Ingestion/Recon/RescrubService.cs`

- [ ] **Step 1: Locate the post-rescrub persistence path**

After the boundary audit completes and the rescrub result is built, look up the `LibraryVersionRecord` and set `BoundaryIssuePct` from `result.BoundaryIssuePct` (if `RescrubResult` exposes it; otherwise compute as `100.0 * BoundaryIssueCount / Math.Max(1, ChunkCount)`).

- [ ] **Step 2: Persist via `ILibraryRepository.UpsertVersionAsync`**

```csharp
var versionRecord = await libraryRepo.GetVersionAsync(library, version, ct);
if (versionRecord != null)
{
    var updated = versionRecord with { BoundaryIssuePct = pct };
    await libraryRepo.UpsertVersionAsync(updated, ct);
}
```

(The `with` expression may not work if `LibraryVersionRecord` is a class instead of a record. Adjust to mutating the property if it's a class.)

- [ ] **Step 3: Build clean, run rescrub tests**

- [ ] **Step 4: Commit**

```
Persist BoundaryIssuePct on LibraryVersionRecord at end of rescrub

get_library_health and get_dashboard_index can now report
boundary-issue rate without re-running the audit.
```

---

### Task B3: Add aggregation methods to `IChunkRepository`

**Files:**
- Modify: `IChunkRepository.cs`
- Modify: `ChunkRepository.cs`

- [ ] **Step 1: Add interface methods**

```csharp
Task<IReadOnlyDictionary<string, double>> GetLanguageMixAsync(string libraryId, string version, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, int>> GetHostnameDistributionAsync(string libraryId, string version, CancellationToken ct = default);
Task<IReadOnlyList<string>> GetSampleTitlesAsync(string libraryId, string version, int limit, CancellationToken ct = default);
```

- [ ] **Step 2: Implement `GetLanguageMixAsync`**

```csharp
public async Task<IReadOnlyDictionary<string, double>> GetLanguageMixAsync(string libraryId, string version, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var filter = Builders<DocChunk>.Filter.And(
        Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
        Builders<DocChunk>.Filter.Eq(c => c.Version, version)
    );
    var pipeline = mContext.Chunks.Aggregate()
        .Match(filter)
        .Group(c => c.CodeLanguage ?? "unfenced",
               g => new { Language = g.Key, Count = g.Count() });
    var groups = await pipeline.ToListAsync(ct);
    var total = (double) groups.Sum(g => g.Count);
    var mix = groups.ToDictionary(g => g.Language, g => total == 0 ? 0.0 : g.Count / total);
    return mix;
}
```

- [ ] **Step 3: Implement `GetHostnameDistributionAsync`**

```csharp
public async Task<IReadOnlyDictionary<string, int>> GetHostnameDistributionAsync(string libraryId, string version, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var filter = Builders<DocChunk>.Filter.And(
        Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
        Builders<DocChunk>.Filter.Eq(c => c.Version, version)
    );
    var chunks = await mContext.Chunks.Find(filter).Project(c => c.PageUrl).ToListAsync(ct);
    var dist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var url in chunks)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "(unknown)";
        dist[host] = dist.GetValueOrDefault(host) + 1;
    }
    return dist;
}
```

- [ ] **Step 4: Implement `GetSampleTitlesAsync`**

```csharp
public async Task<IReadOnlyList<string>> GetSampleTitlesAsync(string libraryId, string version, int limit, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var filter = Builders<DocChunk>.Filter.And(
        Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
        Builders<DocChunk>.Filter.Eq(c => c.Version, version)
    );
    var titles = await mContext.Chunks.Find(filter)
                               .Project(c => c.PageTitle)
                               .Limit(limit)
                               .ToListAsync(ct);
    var distinct = titles.Distinct().Take(limit).ToList();
    return distinct;
}
```

- [ ] **Step 5: Build clean, commit**

```
Add chunk repo aggregations for library_health and URL_SUSPECT

GetLanguageMixAsync (Mongo $group on CodeLanguage),
GetHostnameDistributionAsync (in-memory roll-up of PageUrl hosts),
GetSampleTitlesAsync (limited PageTitle projection). Used by
get_library_health, get_dashboard_index, and the URL_SUSPECT
payload in start_ingest.
```

---

### Task B4: Create `HealthTools.cs` with `get_library_health`

**Files:**
- Create: `SaddleRAG.Mcp/Tools/HealthTools.cs`
- Test: `SaddleRAG.Tests/Mcp/HealthToolsTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;
using NSubstitute;

namespace SaddleRAG.Tests.Mcp;

public sealed class HealthToolsTests
{
    [Fact]
    public async Task GetLibraryHealth_ReturnsExpectedShape()
    {
        var (factory, libraryRepo, chunkRepo, pageRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord { Id = "foo", Name = "f", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } });
        libraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "foo/1.0", LibraryId = "foo", Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 50, ChunkCount = 250,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768,
                                    BoundaryIssuePct = 7.0
                                });
        chunkRepo.GetLanguageMixAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, double> { ["csharp"] = 0.8, ["unfenced"] = 0.2 });
        chunkRepo.GetHostnameDistributionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, int> { ["docs.foo.com"] = 50 });

        var json = await HealthTools.GetLibraryHealth(factory, library: "foo", version: null, profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"chunkCount\": 250", json);
        Assert.Contains("\"boundaryIssuePct\": 7", json);
        Assert.Contains("\"languageMix\":", json);
        Assert.Contains("\"hint\": \"rechunk_library may help\"", json);  // 5 ≤ 7 < 10
    }

    private static (RepositoryFactory factory, ILibraryRepository libraryRepo, IChunkRepository chunkRepo, IPageRepository pageRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        return (factory, libraryRepo, chunkRepo, pageRepo);
    }
}
```

- [ ] **Step 2: Implement `HealthTools.cs`**

```csharp
// HealthTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

[McpServerToolType]
public static class HealthTools
{
    [McpServerTool(Name = "get_library_health")]
    [Description("Per-version diagnostic snapshot. Returns chunk count, hostname " +
                 "distribution, language mix, boundary-issue rate, and suspect markers. " +
                 "For the actual library content, use get_library_overview instead."
                )]
    public static async Task<string> GetLibraryHealth(RepositoryFactory repositoryFactory,
                                                      [Description("Library identifier")]
                                                      string library,
                                                      [Description("Specific version — defaults to current")]
                                                      string? version = null,
                                                      [Description("Optional database profile name")]
                                                      string? profile = null,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);

        var lib = await libraryRepo.GetLibraryAsync(library, ct);
        string result;
        if (lib == null)
            result = JsonSerializer.Serialize(new { Error = $"Library '{library}' not found." }, smJsonOptions);
        else
        {
            var resolvedVersion = version ?? lib.CurrentVersion;
            var versionRecord = await libraryRepo.GetVersionAsync(library, resolvedVersion, ct);
            if (versionRecord == null)
                result = JsonSerializer.Serialize(new { Error = $"Version '{resolvedVersion}' not found." }, smJsonOptions);
            else
            {
                var languageMix = await chunkRepo.GetLanguageMixAsync(library, resolvedVersion, ct);
                var hostnames = await chunkRepo.GetHostnameDistributionAsync(library, resolvedVersion, ct);

                var (boundaryHint, boundaryHintMessage) = ResolveBoundaryHint(versionRecord.BoundaryIssuePct);

                var response = new
                                   {
                                       library,
                                       version = resolvedVersion,
                                       currentVersion = lib.CurrentVersion,
                                       lastScrapedAt = versionRecord.ScrapedAt,
                                       chunkCount = versionRecord.ChunkCount,
                                       pageCount = versionRecord.PageCount,
                                       distinctHostCount = hostnames.Count,
                                       hostnames = hostnames.OrderByDescending(kv => kv.Value).Take(20)
                                                            .Select(kv => new { host = kv.Key, count = kv.Value }),
                                       languageMix,
                                       boundaryIssuePct = versionRecord.BoundaryIssuePct,
                                       suspect = versionRecord.Suspect,        // Filled by Track E
                                       suspectReasons = versionRecord.SuspectReasons,
                                       boundaryHint = new { hint = boundaryHint, message = boundaryHintMessage }
                                   };
                result = JsonSerializer.Serialize(response, smJsonOptions);
            }
        }
        return result;
    }

    private static (string? hint, string? message) ResolveBoundaryHint(double pct)
    {
        (string? hint, string? message) result;
        if (pct >= 10.0)
            result = ("rechunk_recommended", "rechunk_library recommended");
        else if (pct >= 5.0)
            result = ("rechunk_may_help", "rechunk_library may help");
        else
            result = (null, null);
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
```

> **Note:** This tool references `versionRecord.Suspect` and `versionRecord.SuspectReasons`, which are added by Track E (E1). If Track E hasn't landed yet when implementing this task, add the two fields to `LibraryVersionRecord` now (with default `false` and empty list) so the build succeeds. The detector hook lands later.

- [ ] **Step 3: Build, run tests, expect pass**

- [ ] **Step 4: Commit**

```
Add get_library_health MCP tool

Per-version diagnostic snapshot reading from LibraryVersionRecord
plus chunk-repo aggregations (languageMix, hostname distribution).
Boundary hint tiered at 5% / 10% per spec. Suspect/SuspectReasons
fields are wired in (defaults until Track E populates them).
```

---

## Track E — URL Sanity & Suspect Detection

### Task E1: Add Suspect fields to `LibraryVersionRecord` and `IngestStatus.UrlSuspect`

**Files:**
- Modify: `SaddleRAG.Core/Models/LibraryVersionRecord.cs` — add three fields (may already be there from B4 if implemented strictly in order)
- Modify: `SaddleRAG.Core/Enums/IngestStatus.cs` — add `UrlSuspect`

- [ ] **Step 1: Ensure fields**

In `LibraryVersionRecord.cs`:

```csharp
public bool Suspect { get; set; }
public IReadOnlyList<string> SuspectReasons { get; set; } = Array.Empty<string>();
public DateTime? LastSuspectEvaluatedAt { get; set; }
```

- [ ] **Step 2: Add `IngestStatus.UrlSuspect`**

- [ ] **Step 3: Build, commit**

```
Add Suspect fields and IngestStatus.UrlSuspect

Foundation for Track E's post-scrape detector and start_ingest's
URL_SUSPECT branch.
```

---

### Task E2: Add `SetSuspectAsync` / `ClearSuspectAsync` to `ILibraryRepository`

**Files:**
- Modify: `ILibraryRepository.cs`, `LibraryRepository.cs`

- [ ] **Step 1: Add methods**

```csharp
Task SetSuspectAsync(string libraryId, string version, IReadOnlyList<string> reasons, CancellationToken ct = default);
Task ClearSuspectAsync(string libraryId, string version, CancellationToken ct = default);
```

Implementation:

```csharp
public async Task SetSuspectAsync(string libraryId, string version, IReadOnlyList<string> reasons, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);
    ArgumentNullException.ThrowIfNull(reasons);

    var filter = Builders<LibraryVersionRecord>.Filter.And(
        Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId),
        Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
    );
    var update = Builders<LibraryVersionRecord>.Update
        .Set(v => v.Suspect, true)
        .Set(v => v.SuspectReasons, reasons)
        .Set(v => v.LastSuspectEvaluatedAt, DateTime.UtcNow);
    await mContext.LibraryVersions.UpdateOneAsync(filter, update, cancellationToken: ct);
}

public async Task ClearSuspectAsync(string libraryId, string version, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);

    var filter = Builders<LibraryVersionRecord>.Filter.And(
        Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId),
        Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
    );
    var update = Builders<LibraryVersionRecord>.Update
        .Set(v => v.Suspect, false)
        .Set(v => v.SuspectReasons, Array.Empty<string>())
        .Set(v => v.LastSuspectEvaluatedAt, DateTime.UtcNow);
    await mContext.LibraryVersions.UpdateOneAsync(filter, update, cancellationToken: ct);
}
```

- [ ] **Step 2: Build, commit**

```
Add ILibraryRepository.SetSuspectAsync / ClearSuspectAsync

Used by SuspectDetector at end of pipeline and by
submit_url_correction when re-rooting a scrape.
```

---

### Task E3: Implement `SuspectDetector`

**Files:**
- Create: `SaddleRAG.Ingestion/Suspect/SuspectReason.cs`
- Create: `SaddleRAG.Ingestion/Suspect/SuspectDetector.cs`
- Test: `SaddleRAG.Tests/Suspect/SuspectDetectorTests.cs`

- [ ] **Step 1: String constants**

```csharp
// SuspectReason.cs
namespace SaddleRAG.Ingestion.Suspect;

public static class SuspectReason
{
    public const string OnePager = "OnePager";
    public const string SparseLinkGraph = "SparseLinkGraph";
    public const string SingleHost = "SingleHost";
    public const string LanguageMismatch = "LanguageMismatch";
    public const string ReadmeOnly = "ReadmeOnly";
}
```

- [ ] **Step 2: Detector**

```csharp
// SuspectDetector.cs
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Suspect;

public sealed class SuspectDetector
{
    public async Task<IReadOnlyList<string>> EvaluateAsync(string libraryId,
                                                            string version,
                                                            string rootUrl,
                                                            int pageCount,
                                                            int distinctHostCount,
                                                            int distinctLinkTargets,
                                                            IReadOnlyDictionary<string, double> languageMix,
                                                            IReadOnlyList<string> declaredLanguages,
                                                            IReadOnlyList<string> sampleTitles,
                                                            CancellationToken ct = default)
    {
        var reasons = new List<string>();

        if (pageCount <= OnePagerThreshold)
            reasons.Add(SuspectReason.OnePager);

        if (distinctLinkTargets < SparseLinkThreshold)
            reasons.Add(SuspectReason.SparseLinkGraph);

        if (distinctHostCount == 1 && declaredLanguages.Count > 1)
            reasons.Add(SuspectReason.SingleHost);

        if (declaredLanguages.Count > 0)
        {
            bool anyDeclaredAboveThreshold = declaredLanguages.Any(d =>
                languageMix.GetValueOrDefault(d.ToLowerInvariant(), 0.0) >= LanguageMatchThreshold);
            if (!anyDeclaredAboveThreshold)
                reasons.Add(SuspectReason.LanguageMismatch);
        }

        bool isGitHubRoot = Uri.TryCreate(rootUrl, UriKind.Absolute, out var u)
                            && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        bool readmeOnly = isGitHubRoot && sampleTitles.All(t =>
            t.Contains("readme", StringComparison.OrdinalIgnoreCase));
        if (readmeOnly)
            reasons.Add(SuspectReason.ReadmeOnly);

        return reasons;
    }

    private const int OnePagerThreshold = 3;
    private const int SparseLinkThreshold = 10;
    private const double LanguageMatchThreshold = 0.30;
}
```

- [ ] **Step 3: Tests**

```csharp
using SaddleRAG.Ingestion.Suspect;

namespace SaddleRAG.Tests.Suspect;

public sealed class SuspectDetectorTests
{
    [Fact]
    public async Task OnePager_FlagsBelowThreshold()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://example.com",
            pageCount: 2, distinctHostCount: 1, distinctLinkTargets: 50,
            languageMix: new Dictionary<string, double> { ["csharp"] = 1.0 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "About" },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(SuspectReason.OnePager, reasons);
    }

    [Fact]
    public async Task LanguageMismatch_FlagsWhenNoDeclaredLanguageAbove30Percent()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://example.com",
            pageCount: 100, distinctHostCount: 1, distinctLinkTargets: 50,
            languageMix: new Dictionary<string, double> { ["go"] = 0.5, ["ruby"] = 0.5 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "Some doc" },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(SuspectReason.LanguageMismatch, reasons);
    }

    [Fact]
    public async Task HealthyLibrary_NoReasons()
    {
        var d = new SuspectDetector();
        var reasons = await d.EvaluateAsync("lib", "1.0", "https://docs.example.com",
            pageCount: 500, distinctHostCount: 3, distinctLinkTargets: 1000,
            languageMix: new Dictionary<string, double> { ["csharp"] = 0.9 },
            declaredLanguages: new[] { "csharp" },
            sampleTitles: new[] { "Tutorial", "Reference" },
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(reasons);
    }
}
```

- [ ] **Step 4: Build, run tests, expect pass**

- [ ] **Step 5: Commit**

```
Add SuspectDetector with five heuristics

OnePager (≤3 pages), SparseLinkGraph (<10 distinct targets),
SingleHost (single host but multiple declared languages),
LanguageMismatch (no declared language hits 0.30 of mix),
ReadmeOnly (github.com root + readme-titled sample pages). Pure
function class, no I/O — easy to unit test. Hooked into the
ingestion orchestrator in the next commit.
```

---

### Task E4: Hook `SuspectDetector` into `IngestionOrchestrator.UpdateLibraryMetadataAsync`

**Files:**
- Modify: `SaddleRAG.Ingestion/IngestionOrchestrator.cs`

- [ ] **Step 1: Inject `SuspectDetector` and `IChunkRepository` into orchestrator constructor**

If the orchestrator already has these, skip. Otherwise add to its constructor and store as `m`-prefixed fields. Register the detector in DI in `Program.cs`.

- [ ] **Step 2: Call detector at end of `UpdateLibraryMetadataAsync`**

After the existing `UpsertVersionAsync(versionRecord, ct)`:

```csharp
var languageMix = await mChunkRepository.GetLanguageMixAsync(job.LibraryId, job.Version, ct);
var hostnameDist = await mChunkRepository.GetHostnameDistributionAsync(job.LibraryId, job.Version, ct);
var sampleTitles = await mChunkRepository.GetSampleTitlesAsync(job.LibraryId, job.Version, limit: 5, ct);

var profile = await mLibraryProfileRepository.GetAsync(job.LibraryId, job.Version, ct);
var declaredLanguages = profile?.Languages ?? Array.Empty<string>();

var distinctLinkTargets = await mPageRepository.GetDistinctOutboundLinkCountAsync(job.LibraryId, job.Version, ct);
// If GetDistinctOutboundLinkCountAsync doesn't exist yet, fall back to a simple bound:
// use distinctLinkTargets = pageCount * AveragePageOutboundLinks ≈ pageCount (rough). The exact
// number doesn't matter unless it triggers SparseLinkGraph.

var reasons = await mSuspectDetector.EvaluateAsync(job.LibraryId,
                                                    job.Version,
                                                    job.RootUrl,
                                                    pageCount: progress.PagesCompleted,
                                                    distinctHostCount: hostnameDist.Count,
                                                    distinctLinkTargets: distinctLinkTargets,
                                                    languageMix: languageMix,
                                                    declaredLanguages: declaredLanguages,
                                                    sampleTitles: sampleTitles,
                                                    ct);

if (reasons.Count > 0)
    await mLibraryRepository.SetSuspectAsync(job.LibraryId, job.Version, reasons, ct);
else
    await mLibraryRepository.ClearSuspectAsync(job.LibraryId, job.Version, ct);
```

> If `IPageRepository.GetDistinctOutboundLinkCountAsync` does not exist, leave `distinctLinkTargets` at a placeholder value (e.g., `int.MaxValue`) so `SparseLinkGraph` will not fire. Add the method as a follow-up; the other four reasons still cover the common cases.

- [ ] **Step 3: Update DI registration in `Program.cs` to register `SuspectDetector` as singleton.**

- [ ] **Step 4: Build clean, commit**

```
Hook SuspectDetector into IngestionOrchestrator

End-of-pipeline evaluation runs after metadata upsert; sets or
clears LibraryVersionRecord.Suspect / SuspectReasons. The five
heuristics now fire automatically at the close of every scrape.
```

---

### Task E5: Refuse `scrape_docs(resume=true)` on suspect libraries

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs`

- [ ] **Step 1: Lookup `LibraryVersionRecord.Suspect` and refuse**

In the `resume=true` branch, after looking up `previous`:

```csharp
var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
var versionRecord = await libraryRepo.GetVersionAsync(libraryId, version, ct);
if (versionRecord != null && versionRecord.Suspect)
{
    var refused = new
                      {
                          Status = "Refused",
                          Reason = "URL_SUSPECT",
                          SuspectReasons = versionRecord.SuspectReasons,
                          Hint = "Call submit_url_correction(library, version, newUrl) with a corrected URL."
                      };
    return JsonSerializer.Serialize(refused, new JsonSerializerOptions { WriteIndented = true });
}
```

- [ ] **Step 2: Build, commit**

```
Refuse scrape_docs(resume=true) on suspect libraries

Resuming a scrape rooted at a wrong URL just re-crawls the wrong
site. Refusal points at submit_url_correction, which clears the
suspect flag and re-queues against a corrected URL.
```

---

### Task E6: `submit_url_correction` MCP tool

**Files:**
- Create: `SaddleRAG.Mcp/Tools/UrlCorrectionTools.cs`
- Test: `SaddleRAG.Tests/Mcp/UrlCorrectionToolsTests.cs`

- [ ] **Step 1: Test (failing)**

```csharp
[Fact]
public async Task SubmitUrlCorrection_DryRun_ReportsCascadeWithoutWriting()
{
    var (factory, libraryRepo, runner, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo, excludedRepo) = MakeFactory();
    libraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(new LibraryVersionRecord
                            {
                                Id = "foo/1.0",
                                LibraryId = "foo",
                                Version = "1.0",
                                ScrapedAt = DateTime.UtcNow,
                                PageCount = 1,
                                ChunkCount = 50,
                                EmbeddingProviderId = "ollama",
                                EmbeddingModelName = "nomic-embed-text",
                                EmbeddingDimensions = 768,
                                Suspect = true,
                                SuspectReasons = new[] { "OnePager" }
                            });
    chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50);

    var json = await UrlCorrectionTools.SubmitUrlCorrection(factory, runner,
                                                            library: "foo", version: "1.0", newUrl: "https://docs.foo.com",
                                                            dryRun: true, profile: null,
                                                            ct: TestContext.Current.CancellationToken);
    Assert.Contains("\"DryRun\": true", json);
    await runner.DidNotReceive().QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task SubmitUrlCorrection_Apply_DropsAndRequeues()
{
    var (factory, libraryRepo, runner, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo, excludedRepo) = MakeFactory();
    runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("new-job-id");

    var json = await UrlCorrectionTools.SubmitUrlCorrection(factory, runner,
                                                            library: "foo", version: "1.0", newUrl: "https://docs.foo.com",
                                                            dryRun: false, profile: null,
                                                            ct: TestContext.Current.CancellationToken);
    Assert.Contains("\"JobId\": \"new-job-id\"", json);
    await chunkRepo.Received(1).DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
    await libraryRepo.Received(1).ClearSuspectAsync("foo", "1.0", Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Implement**

```csharp
// UrlCorrectionTools.cs
[McpServerToolType]
public static class UrlCorrectionTools
{
    [McpServerTool(Name = "submit_url_correction")]
    [Description("Re-root a scrape at a corrected URL. Drops the existing chunks, " +
                 "pages, profile, indexes, and bm25 shards for (library, version), " +
                 "clears the Suspect flag, then queues a fresh scrape_docs at newUrl. " +
                 "Use this when start_ingest returned URL_SUSPECT and you've identified " +
                 "a better docs URL. Defaults to dryRun=false (this is the recovery path)."
                )]
    public static async Task<string> SubmitUrlCorrection(RepositoryFactory repositoryFactory,
                                                         ScrapeJobRunner runner,
                                                         [Description("Library identifier")]
                                                         string library,
                                                         [Description("Version")]
                                                         string version,
                                                         [Description("Corrected docs root URL")]
                                                         string newUrl,
                                                         [Description("If true, preview without writing or queueing.")]
                                                         bool dryRun = false,
                                                         [Description("Optional database profile name")]
                                                         string? profile = null,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(newUrl);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var pageRepo = repositoryFactory.GetPageRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);

        string result;
        if (dryRun)
        {
            var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
            var pages = await pageRepo.GetPageCountAsync(library, version, ct);
            var preview = new
                              {
                                  DryRun = true,
                                  WouldDelete = new { Chunks = chunks, Pages = pages, Profiles = 1, Indexes = 1, Bm25Shards = 1 },
                                  WouldQueue = new { RootUrl = newUrl, Library = library, Version = version }
                              };
            result = JsonSerializer.Serialize(preview, smJsonOptions);
        }
        else
        {
            var chunks = await chunkRepo.DeleteChunksAsync(library, version, ct);
            var pages = await pageRepo.DeleteAsync(library, version, ct);
            await profileRepo.DeleteAsync(library, version, ct);
            await indexRepo.DeleteAsync(library, version, ct);
            await bm25Repo.DeleteAsync(library, version, ct);
            await libraryRepo.ClearSuspectAsync(library, version, ct);

            var job = ScrapeJobFactory.CreateFromUrl(newUrl, library, version, hint: "(corrected URL)",
                                                     maxPages: 0, fetchDelayMs: 500, forceClean: true);
            var jobId = await runner.QueueAsync(job, profile, ct);

            var response = new
                               {
                                   DryRun = false,
                                   Cleared = new { Chunks = chunks, Pages = pages },
                                   JobId = jobId,
                                   Status = "Queued",
                                   Message = $"Suspect chunks dropped, scrape re-queued at {newUrl}. Poll get_scrape_status with jobId='{jobId}'."
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
```

- [ ] **Step 3: Run tests, expect pass**

- [ ] **Step 4: Commit**

```
Add submit_url_correction MCP tool

Recon-style callback for URL_SUSPECT recovery: drops the suspect
chunks/pages/profile/indexes/shards, clears the Suspect flag, and
queues a fresh scrape rooted at the corrected URL. dryRun=true
previews the cascade without writing. dryRun=false is the default
because this is the recovery path — the LLM has already been told
the URL is wrong.
```

---

### Task E7: Wire `URL_SUSPECT` into `start_ingest`

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/IngestTools.cs`

- [ ] **Step 1: Lookup `LibraryVersionRecord.Suspect` and add a higher-precedence branch**

In `StartIngest`, after the `IN_PROGRESS` check, add:

```csharp
var versionRecord = await libraryRepo.GetVersionAsync(library, version, ct);
if (versionRecord != null && versionRecord.Suspect)
{
    var sampleTitles = await chunkRepo.GetSampleTitlesAsync(library, version, limit: 5, ct);
    var hostnameDist = await chunkRepo.GetHostnameDistributionAsync(library, version, ct);
    var suspectResponse = MakeUrlSuspect(library, version, url, versionRecord.SuspectReasons, sampleTitles, hostnameDist);
    return JsonSerializer.Serialize(suspectResponse, smJsonOptions);
}
```

Add `MakeUrlSuspect`:

```csharp
private static IngestStatusResponse MakeUrlSuspect(string library, string version, string url,
                                                   IReadOnlyList<string> suspectReasons,
                                                   IReadOnlyList<string> sampleTitles,
                                                   IReadOnlyDictionary<string, int> hostnameDist) =>
    new()
        {
            Status = IngestStatus.UrlSuspect,
            LibraryId = library,
            Version = version,
            Url = url,
            NextTool = "submit_url_correction",
            Message = $"Indexed content looks wrong: {string.Join(", ", suspectReasons)}. " +
                      $"Sample titles: {string.Join("; ", sampleTitles.Take(3))}. " +
                      $"Hostnames: {string.Join(", ", hostnameDist.Keys.Take(5))}. " +
                      "Browse the URL and call submit_url_correction with a better one if needed.",
            NextToolArgs = new Dictionary<string, string>
                               {
                                   ["library"] = library,
                                   ["version"] = version,
                                   ["newUrl"] = "(your corrected URL here)"
                               }
        };
```

- [ ] **Step 2: Build, commit**

```
Wire URL_SUSPECT branch into start_ingest

Higher precedence than RECON_NEEDED / READY_TO_SCRAPE / STALE /
READY (only IN_PROGRESS beats it). Returns sample page titles and
hostname distribution as context so the calling LLM can judge
whether the URL is wrong, with submit_url_correction as the
nextTool.
```

---

### Task E8: Add `BoundaryHint` to `rescrub_library` output

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/RescrubTools.cs`

- [ ] **Step 1: Wrap result with hint**

After `var result = await service.RescrubAsync(...)`, before serializing:

```csharp
var pct = result.BoundaryIssuePct;  // or compute as 100 * BoundaryIssueCount / Math.Max(1, ChunkCount)

string? hint;
if (pct >= 10.0) hint = "rechunk_library recommended";
else if (pct >= 5.0) hint = "rechunk_library may help";
else hint = null;

var responseWithHint = new
                           {
                               Result = result,
                               BoundaryHint = new { pct, hint }
                           };
var json = JsonSerializer.Serialize(responseWithHint, smJsonOptions);
```

(If `RescrubResult` is a record, you can return it as-is and add a sibling property; pick whichever shape doesn't break existing test assertions.)

- [ ] **Step 2: Build, commit**

```
Add BoundaryHint to rescrub_library output

Two-tier hint: 5–10% → "rechunk_library may help",
≥10% → "rechunk_library recommended". Same logic as
get_library_health so callers see consistent guidance from both
tools.
```

---

## Track A — Cold Start

### Task A1: `list_libraries` empty-state hint

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/LibraryTools.cs`

- [ ] **Step 1: Wrap empty result**

```csharp
public static async Task<string> ListLibraries(RepositoryFactory repositoryFactory, string? profile = null, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(repositoryFactory);

    var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
    var libraries = await libraryRepository.GetAllLibrariesAsync(ct);

    string result;
    if (libraries.Count == 0)
    {
        var emptyResponse = new
                                {
                                    Libraries = Array.Empty<object>(),
                                    Hint = "Database is empty. Call get_dashboard_index for orientation, " +
                                           "or use index_project_dependencies(path=...) / scrape_docs(url=..., libraryId=..., version=...) to ingest."
                                };
        result = JsonSerializer.Serialize(emptyResponse, smJsonOptions);
    }
    else
        result = JsonSerializer.Serialize(libraries, smJsonOptions);

    return result;
}
```

- [ ] **Step 2: Build, commit**

```
Add empty-state hint to list_libraries

When the database has zero libraries, return a {Libraries: [], Hint: "..."}
shape that points at get_dashboard_index and the two ingestion paths.
The Hint field is omitted on populated databases — back-compat
preserved.
```

---

### Task A2: `get_dashboard_index` MCP tool

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/HealthTools.cs` — add `GetDashboardIndex`
- Test: append to `HealthToolsTests.cs`

- [ ] **Step 1: Test (failing)**

```csharp
[Fact]
public async Task GetDashboardIndex_EmptyDb_RecommendsIngestion()
{
    var (factory, libraryRepo, _, _) = MakeFactory();
    libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryRecord>());
    var jobRepo = Substitute.For<IScrapeJobRepository>();
    factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
    jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ScrapeJobRecord>());

    var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                  ct: TestContext.Current.CancellationToken);

    Assert.Contains("\"libraryCount\": 0", json);
    Assert.Contains("index_project_dependencies", json);
}

[Fact]
public async Task GetDashboardIndex_PopulatedDb_AggregatesAcrossLibraries()
{
    var (factory, libraryRepo, _, _) = MakeFactory();
    libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
               .Returns(new[]
                            {
                                new LibraryRecord { Id = "a", Name = "a", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } },
                                new LibraryRecord { Id = "b", Name = "b", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } }
                            });
    libraryRepo.GetVersionAsync("a", "1.0", Arg.Any<CancellationToken>())
               .Returns(new LibraryVersionRecord
                            {
                                Id = "a/1.0", LibraryId = "a", Version = "1.0",
                                ScrapedAt = DateTime.UtcNow,
                                PageCount = 100, ChunkCount = 500,
                                EmbeddingProviderId = "ollama",
                                EmbeddingModelName = "nomic-embed-text",
                                EmbeddingDimensions = 768,
                                Suspect = true,
                                SuspectReasons = new[] { "LanguageMismatch" }
                            });
    libraryRepo.GetVersionAsync("b", "1.0", Arg.Any<CancellationToken>())
               .Returns(new LibraryVersionRecord
                            {
                                Id = "b/1.0", LibraryId = "b", Version = "1.0",
                                ScrapedAt = DateTime.UtcNow,
                                PageCount = 100, ChunkCount = 500,
                                EmbeddingProviderId = "ollama",
                                EmbeddingModelName = "nomic-embed-text",
                                EmbeddingDimensions = 768,
                                Suspect = false,
                                SuspectReasons = Array.Empty<string>()
                            });
    var jobRepo = Substitute.For<IScrapeJobRepository>();
    factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
    jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ScrapeJobRecord>());

    var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                  ct: TestContext.Current.CancellationToken);

    Assert.Contains("\"libraryCount\": 2", json);
    Assert.Contains("\"suspectCount\": 1", json);
    Assert.Contains("\"a\"", json);
}
```

- [ ] **Step 2: Implement**

```csharp
[McpServerTool(Name = "get_dashboard_index")]
[Description("Single-call SaddleRAG status overview. Returns library/version counts, " +
             "recent scrape jobs (with stale-running flags), suspect/stale library " +
             "lists (capped at 20), and a SuggestedNextAction. The documented entry " +
             "point for fresh sessions."
            )]
public static async Task<string> GetDashboardIndex(RepositoryFactory repositoryFactory,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(repositoryFactory);

    var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
    var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);

    var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
    var recentJobs = await jobRepo.ListRecentAsync(limit: 5, ct);

    var suspectList = new List<object>();
    var staleList = new List<object>();
    int versionCount = 0;
    foreach (var lib in libraries)
    {
        foreach (var v in lib.AllVersions)
        {
            versionCount++;
            var versionRecord = await libraryRepo.GetVersionAsync(lib.Id, v, ct);
            if (versionRecord == null) continue;
            if (versionRecord.Suspect && suspectList.Count < 20)
                suspectList.Add(new { library = lib.Id, version = v, reasons = versionRecord.SuspectReasons });
            // Stale detection (parser version drift): require parser-version comparison; for
            // now, skip — Track B persists only BoundaryIssuePct, not parser drift state.
        }
    }

    var staleRunning = recentJobs
        .Where(j => j.Status == ScrapeJobStatus.Running && j.LastProgressAt.HasValue
                    && DateTime.UtcNow - j.LastProgressAt.Value > TimeSpan.FromHours(StaleRunningThresholdHours))
        .ToList();

    object suggested;
    if (libraries.Count == 0)
        suggested = new { tool = "scrape_docs", message = "Database is empty. Ingest a library to begin." };
    else if (suspectList.Count > 0)
        suggested = new { tool = "submit_url_correction", message = $"{suspectList.Count} suspect libraries — review and correct URLs." };
    else if (staleRunning.Count > 0)
        suggested = new { tool = "cancel_scrape", message = $"{staleRunning.Count} jobs have not progressed in over {StaleRunningThresholdHours}h." };
    else
        suggested = new { tool = (string?) null, message = "All libraries look healthy." };

    var response = new
                       {
                           libraryCount = libraries.Count,
                           versionCount,
                           recentJobs = recentJobs.Select(j => new
                                                                  {
                                                                      j.Id,
                                                                      j.Status,
                                                                      Library = j.Job.LibraryId,
                                                                      j.Job.Version,
                                                                      stale = j.Status == ScrapeJobStatus.Running
                                                                              && j.LastProgressAt.HasValue
                                                                              && DateTime.UtcNow - j.LastProgressAt.Value > TimeSpan.FromHours(StaleRunningThresholdHours),
                                                                      j.LastProgressAt
                                                                  }),
                           suspectCount = suspectList.Count,
                           suspectLibraries = suspectList,
                           staleCount = staleList.Count,
                           staleLibraries = staleList,
                           suggestedNextAction = suggested
                       };
    var json = JsonSerializer.Serialize(response, smJsonOptions);
    return json;
}

private const int StaleRunningThresholdHours = 4;
```

- [ ] **Step 3: Run tests, expect pass**

- [ ] **Step 4: Commit**

```
Add get_dashboard_index MCP tool

Single-call orientation tool: library count, version count, recent
jobs (with stale-running flag at 4h LastProgressAt threshold),
suspect library list capped at 20, suggested next action. The
documented entry point for fresh sessions and the natural follow-up
when list_libraries returns empty.
```

---

## Final integration pass

### Task FINAL: Build clean, full test run, branch ship-readiness

- [ ] **Step 1: Full warning-free build**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`
Expected: SUCCESS, zero warnings.

- [ ] **Step 2: Full test run**

Run: `dotnet test SaddleRAG.slnx --configuration Release --no-build`
Expected: ALL TESTS PASS.

- [ ] **Step 3: Manual smoke via MCP server**

Optional but recommended: start the MCP server locally, exercise the canonical session flows from the spec (Flow A, Flow B, Flow C). Confirm that:
- `get_dashboard_index` returns sensible output on a populated DB
- `library_health` shows language mix and boundary hint
- `rename_library(dryRun=true)` previews counts; `dryRun=false` actually renames
- `cancel_scrape` works on the still-Running mongodb.driver job (or the orphaned 89ccd7e9 from Apr 14)
- `submit_url_correction` re-queues mongodb.driver at a corrected URL

- [ ] **Step 4: Final commit (if smoke test surfaced fixes)**

If anything tweaked during smoke, commit. Otherwise no commit needed.

- [ ] **Step 5: Branch is ready for review**

```
Branch feature/mcp-tool-ux ready for review.
- 7 tracks, ~38 commits, +8 / -6 / 4 modified MCP tools
- Spec at docs/superpowers/specs/2026-04-27-mcp-tool-ux-design.md
- All tests pass, warning-free build under TreatWarningsAsErrors
```

Open a PR against `master`. PR body summarizes the seven tracks and links the spec.

---

## Spec Coverage Audit

| Spec section | Implementing task(s) |
|---|---|
| Track A (cold start: `get_dashboard_index`, `list_libraries` hint) | A1, A2 |
| Track B (`get_library_health`, languageMix, boundary hint) | B1, B2, B3, B4 |
| Track C (rename/delete library/version, dryRun default) | C1–C9 |
| Track D (collapse `scrape_library` into `scrape_docs`) | G4, G5 |
| Track E (suspect detector, URL_SUSPECT state, `submit_url_correction`) | E1–E8 |
| Track F (`cancel_scrape`, CTS registry, IN_PROGRESS state, LastProgressAt) | F1–F7 |
| Track G (`continue_scrape` → `scrape_docs(resume=true)`, list_symbols) | G1, G2, G3 |
| Data-model changes (LibraryVersionRecord, ScrapeJobRecord, IngestStatus, ScrapeJobStatus) | F1, F2, B1, E1 |
| Cascade order under failure (mitigation: idempotent re-run) | C5, C8, C9 (sequential per-version cascade) |
| Suspect detection false-positive mitigation | E3 (configurable thresholds), E5 (`submit_url_correction` clears flag) |

No spec section is unimplemented.
