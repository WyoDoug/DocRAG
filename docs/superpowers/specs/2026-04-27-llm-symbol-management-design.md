# LLM Symbol-Management Flow (Phase 2) — Design

**Status:** Approved
**Date:** 2026-04-27
**Branch:** chore/dev-tooling

## Goal

Add per-library MCP tools that let a calling LLM review what the symbol extractor rejected, restore wrongly-rejected symbols, and demote noise that slipped through. The tools are always optional — the server hints when reviewing would help; never demands it.

Phase 1 (already shipped on this branch) cleaned up most extractor noise via rule changes (`HasMidWordCapital` fix, `IsLikelyAbbreviation` gate, `UnitsLookup`, expanded `Stoplist`, chunker boundary fix). Phase 2 handles residual noise that's hard to catch with rules — brand names like `RealTek`, garbled fragments like `AFuwU`, common-word misclassifications like `along`/`data`/`enumerator`/`integer` showing up as enums in Aerotech docs.

## Non-Goals

- Auto-suggestion of `LikelySymbols` candidates (deferred — out of MVP).
- LLM-side classification of rejection samples (caller's job, not server's).
- Schema changes outside `LibraryProfile` and the new collection.

## Decisions Log

| # | Decision | Choice |
|---|---|---|
| 1 | Rejection reason taxonomy | Six values: `GlobalStoplist`, `LibraryStoplist`, `Unit`, `BelowMinLength`, `LikelyAbbreviation`, `NoStructureSignal` |
| 2 | Sample-sentence capture | Char-window (200 chars total around match) + spread sampling across corpus thirds + single-pass capture during rescrub |
| 3 | Hints threshold | Excluded ≥ 5% of candidates AND ≥ 20 absolute exclusions |
| 4 | Storage scope | Per-`(library, version)` for `library_excluded_symbols`; `LibraryProfile.Stoplist` carries forward to new versions when the new profile's stoplist is empty |
| 5 | Idempotency / order-of-ops | Mutually-exclusive last-call-wins between `LikelySymbols` and `LibraryProfile.Stoplist`, with override reported in tool response |
| 6 | `LikelyAbbreviation` as a 6th reason | Yes — labeled at capture time when the abbreviation guard was the deciding factor; no extraction-behavior change |

## Data Model

### New enum — `DocRAG.Core/Enums/SymbolRejectionReason.cs`

```
GlobalStoplist     // hit the universal Stoplist
LibraryStoplist    // hit LibraryProfile.Stoplist
Unit               // hit UnitsLookup
BelowMinLength     // length < MinIdentifierLength (2)
LikelyAbbreviation // prose-frequent path was the only way through, blocked by IsLikelyAbbreviation
NoStructureSignal  // failed all keep rules in ShouldKeep()
```

### New record — `DocRAG.Core/Models/ExcludedSymbol.cs`

```csharp
public record ExcludedSymbol
{
    public required string Id { get; init; }                  // "{LibraryId}/{Version}/{Name}"
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required string Name { get; init; }                // exact token text (case preserved)
    public required SymbolRejectionReason Reason { get; init; }
    public required IReadOnlyList<string> SampleSentences { get; init; }   // 0..3 char-window snippets
    public required int ChunkCount { get; init; }             // total chunks where token appeared
    public DateTime CapturedUtc { get; init; }
}
```

### Updated record — `LibraryProfile.cs`

Add field:

```csharp
public IReadOnlyList<string> Stoplist { get; init; } = [];
```

Bump `CurrentSchemaVersion` from 1 → 2. Existing migration handles missing fields as defaults.

### MongoDB collection — `library_excluded_symbols`

Indexes:
- Default unique on `_id` (the composite `{LibraryId}/{Version}/{Name}` string).
- Compound `{ LibraryId: 1, Version: 1, Reason: 1 }` for `list_excluded_symbols` reason filter.
- Compound `{ LibraryId: 1, Version: 1, Name: 1 }` for fast remove-by-name on promotion/demotion.

Created in `DocRagDbContext.EnsureIndexesAsync` alongside existing collections.

## Component Changes

### 1. `Stoplist` — profile-aware overload

`DocRAG.Ingestion/Symbols/Stoplist.cs`:

```csharp
public enum StoplistMatch { None, Global, Library }

public static bool Contains(string candidate);                          // existing, unchanged
public static StoplistMatch Match(string candidate, LibraryProfile profile);
```

`Match` checks the universal stoplist first (returns `Global`), then the profile stoplist (`Library`), else `None`. Profile stoplist match is `OrdinalIgnoreCase` to align with the universal stoplist's friendlier behavior.

### 2. `SymbolExtractor` — return rejections

`DocRAG.Ingestion/Symbols/SymbolExtractor.cs`:

- New record in `DocRAG.Ingestion/Symbols/RejectedToken.cs`:
  ```csharp
  public record RejectedToken
  {
      public required string Name { get; init; }
      public required SymbolRejectionReason Reason { get; init; }
  }
  ```
- `ExtractedSymbols` gets `IReadOnlyList<RejectedToken> Rejected { get; init; } = []`.
- `IsAdmissible(...)` replaced by `GetRejectionReason(...)` returning `SymbolRejectionReason?` (null when admissible).
- Reason resolution order (first match wins, mirrors existing logic exactly):
  1. `Stoplist.Match(... profile)` → `GlobalStoplist` or `LibraryStoplist`.
  2. `UnitsLookup.IsUnit(...)` → `Unit`.
  3. `name.Length < MinIdentifierLength` → `BelowMinLength`.
  4. `!ShouldKeep(...)` → `LikelyAbbreviation` if the abbreviation guard blocked the prose-frequent rule and no other keep signal fired; else `NoStructureSignal`.
- `IsProseFrequent` returns a richer state (`Frequent | NotFrequent | BlockedByAbbreviation`) so the caller can disambiguate `LikelyAbbreviation` from `NoStructureSignal`.
- `Extract()` collects rejections in parallel with kept symbols; default behavior for callers that don't care is preserved by the empty default.

No change to which tokens are kept vs rejected — only the labeling.

### 3. Sample-window helper

New file `DocRAG.Ingestion/Symbols/SampleWindowExtractor.cs`:

- Locates the first occurrence of a token name in chunk content (case-sensitive, exact match).
- Takes ~100 chars before + the token + ~100 chars after, trims to nearest whitespace at each end.
- Collapses internal whitespace, strips code-fence delimiters.
- Caps total length at 200 chars (truncate from the longer side if needed).
- Returns `null` if the token can't be located (defensive).

### 4. Rejection accumulator

New file `DocRAG.Ingestion/Recon/RejectionAccumulator.cs`:

Constructed once per rescrub with the total chunk count. For each `RejectedToken` paired with the chunk it came from:
- Records the reason (first-seen wins; conflicts are rare since reason is deterministic from token shape).
- Increments `ChunkCount` for the name.
- Determines which corpus third (0/1/2) the chunk index falls in. If no sample exists for that third, captures one via `SampleWindowExtractor`.

`Build()` materializes a `List<ExcludedSymbol>` ready for `UpsertManyAsync`.

### 5. `RescrubService` — wire rejections through

`DocRAG.Ingestion/Recon/RescrubService.cs`:

- New `IExcludedSymbolsRepository` parameter on `RescrubAsync` (threaded from `RescrubTools`).
- After loading chunks, build `RejectionAccumulator(totalChunkCount)`.
- Inside `ProcessChunksAsync`, for each chunk pass `extracted.Rejected` and the chunk to the accumulator.
- After the loop, when `!options.DryRun`:
  ```csharp
  await excludedRepo.DeleteAllForLibraryAsync(libraryId, version, ct);
  await excludedRepo.UpsertManyAsync(accumulator.Build(), ct);
  ```
  (Delete-all-then-upsert keeps state consistent; we never accumulate stale rows from prior rescrubs.)
- `RescrubResult` gets two new fields: `int ExcludedCount`, `IReadOnlyList<string> Hints`.
- Hints fire iff `excludedCount >= 20 && excludedCount >= 0.05 * totalCandidates` (where `totalCandidates = keptCount + excludedCount`).

Hint text:
```
Rescrub complete: {Processed} chunks, {kept} candidate tokens kept, {excluded} excluded as likely noise.
If list_classes/list_functions output looks off, refine per-library:
  list_excluded_symbols(library='{lib}', version='{ver}') — review rejections with sample sentences
  add_to_likely_symbols(...) — promote tokens that ARE real symbols
  add_to_stoplist(...) — demote tokens that are noise
  Then call rescrub_library again to apply.
These steps are OPTIONAL — only worth running if symbol coverage looks wrong on spot-check.
```

### 6. Profile carry-forward

`DocRAG.Ingestion/Recon/LibraryProfileService.cs` — when persisting a new profile via `UpsertAsync`:

- If the new profile's `Stoplist` is empty AND a prior version's profile for the same `LibraryId` exists with a non-empty `Stoplist`, copy the prior `Stoplist` onto the new profile before writing.
- LLM-provided non-empty stoplists are never overridden by carry-forward.

### 7. Repository

New `DocRAG.Core/Interfaces/IExcludedSymbolsRepository.cs`:

```csharp
public interface IExcludedSymbolsRepository
{
    Task<IReadOnlyList<ExcludedSymbol>> ListAsync(
        string libraryId, string version,
        SymbolRejectionReason? reason, int limit, CancellationToken ct);

    Task UpsertManyAsync(IEnumerable<ExcludedSymbol> entries, CancellationToken ct);

    Task RemoveAsync(string libraryId, string version, IEnumerable<string> names, CancellationToken ct);

    Task DeleteAllForLibraryAsync(string libraryId, string version, CancellationToken ct);

    Task<int> CountAsync(string libraryId, string version, CancellationToken ct);
}
```

Implementation in `DocRAG.Database/Repositories/ExcludedSymbolsRepository.cs`. Registered in:
- `RepositoryFactory.GetExcludedSymbolsRepository(profile)`.
- `ServiceCollectionExtensions` (singleton, same pattern as other repos).
- `DocRagDbContext.ExcludedSymbols` collection accessor.

`ListAsync` sorts by `ChunkCount` descending so most-prevalent noise surfaces first.

### 8. MCP tools — `DocRAG.Mcp/Tools/SymbolManagementTools.cs`

Static class with `[McpServerToolType]`. Three `[McpServerTool]` methods.

#### `list_excluded_symbols`

```
list_excluded_symbols(
    library: string,
    version: string,
    reason?: SymbolRejectionReason,
    limit?: int = 50,
    profile?: string)
```

Returns:
```json
{
  "Library": "...",
  "Version": "...",
  "TotalExcluded": 184,
  "Returned": 50,
  "Items": [
    { "Name": "along", "Reason": "NoStructureSignal", "ChunkCount": 47, "SampleSentences": ["…","…","…"] }
  ]
}
```

Returns `RECON_NEEDED` shape if no profile exists.

#### `add_to_likely_symbols`

```
add_to_likely_symbols(library: string, version: string, names: string[], profile?: string)
```

Logic:
1. Fail-fast on empty `names`.
2. Read current `LibraryProfile`.
3. Compute new `LikelySymbols = old ∪ names` — union dedup uses `Ordinal` (matches the extractor's `BuildLikelySet`, case-preserving).
4. Compute new `Stoplist = old − names` — subtraction uses `OrdinalIgnoreCase` (matches `Stoplist`'s extraction-time comparer; "foo" demotion removes any case-equivalent entry like "Foo").
5. Persist via `LibraryProfileRepository.UpsertAsync`.
6. `excludedRepo.RemoveAsync(library, version, names)` to wipe now-stale entries.

Returns:
```json
{
  "Library": "...", "Version": "...",
  "Promoted": ["foo","bar"],
  "AlreadyInLikelySymbols": ["existing"],
  "RemovedFromStoplist": ["bar"],
  "Hints": ["Call rescrub_library to apply the changes."]
}
```

#### `add_to_stoplist`

Mirror image of `add_to_likely_symbols`. Same six-step logic, swapped lists. Returns `Demoted`, `AlreadyInStoplist`, `RemovedFromLikelySymbols`. Casing: `Stoplist` union dedup uses `OrdinalIgnoreCase`; `LikelySymbols` subtraction uses `OrdinalIgnoreCase` so any case-equivalent variant is removed.

### 9. Existing tools

`DocRAG.Mcp/Tools/RescrubTools.cs` — output now includes `ExcludedCount` and `Hints` fields (already on `RescrubResult` via Component 5). No signature change. `WithToolsFromAssembly()` already picks up the new tool class.

## Data Flow

```
rescrub_library
  └─ RescrubService.RescrubAsync
       ├─ load chunks
       ├─ build CorpusContext, RejectionAccumulator(totalChunks)
       ├─ for each chunk: SymbolExtractor.Extract → kept + rejected
       │    └─ accumulator records reason, increments ChunkCount, samples thirds
       ├─ persist chunks (existing)
       ├─ excludedRepo.DeleteAllForLibraryAsync
       ├─ excludedRepo.UpsertManyAsync(accumulator.Build())
       └─ compute Hints (5% AND ≥20 thresholds)

list_excluded_symbols
  └─ excludedRepo.ListAsync → sorted by ChunkCount desc

add_to_likely_symbols / add_to_stoplist
  └─ profileRepo.UpsertAsync (mutate LikelySymbols / Stoplist)
  └─ excludedRepo.RemoveAsync (consistency)
  └─ response includes override report
```

## Error Handling

- All three new tools fail-fast on empty `names` (mutation tools) or missing `library`/`version`.
- `list_excluded_symbols` and the mutation tools return the existing `RECON_NEEDED` shape when no profile exists for the (library, version) pair.
- `excludedRepo.RemoveAsync` is idempotent — removing names not in the collection is a no-op.
- `SampleWindowExtractor` returns null defensively when a token can't be located; the accumulator drops null samples silently.
- Profile carry-forward is best-effort — failure to find a prior version is silent, not an error.

## Project Conventions

Code follows the strict CLAUDE.md standards already enforced in this repo:
- Single return per method (use a result variable).
- No if/else if chains — use switch expressions.
- No `continue` — filter via `Where()` or use if-blocks.
- Max 3 nesting levels — extract a method when needed.
- Comments on their own line; XML docs on public members.
- Allman braces; `m`/`sm`/`ps` field prefixes.
- All git commits via `-F msg.txt`; no AI-attribution trailers.

## Testing Strategy

| File | Coverage |
|---|---|
| `DocRAG.Tests/Symbols/SymbolExtractorTests.cs` (extend) | Reason mapping per path; profile-stoplist override of LikelySymbols; `LikelyAbbreviation` vs `NoStructureSignal` disambiguation |
| `DocRAG.Tests/Symbols/StoplistTests.cs` (extend) | Profile-aware `Match` returns correct enum; case-insensitivity on profile stoplist |
| `DocRAG.Tests/Symbols/SampleWindowExtractorTests.cs` (new) | 200-char cap; whitespace trim; token at start/end of chunk; multiple occurrences; missing token returns null |
| `DocRAG.Tests/Recon/RejectionAccumulatorTests.cs` (new) | Thirds bucketing for sample selection; ChunkCount aggregation; first-seen reason wins |
| `DocRAG.Tests/Recon/RescrubServiceTests.cs` (extend) | Hint thresholds (5% AND ≥20); dry-run does not persist; delete-all-then-upsert leaves no stale rows |
| `DocRAG.Tests/Recon/LibraryProfileServiceTests.cs` (extend) | New profile inherits prior version's Stoplist when empty; non-empty Stoplist not overridden |
| `DocRAG.Tests/Mcp/SymbolManagementToolsTests.cs` (new) | `list_excluded_symbols` round-trip; `add_to_likely_symbols`/`add_to_stoplist` mutate and report overrides; both wipe matching excluded entries; empty `names` errors |

## End-to-End Verification

1. `dotnet build e:/GitHub/DocRAG/DocRAG.slnx -c Debug --nologo -v minimal`
2. `dotnet test e:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --no-build -v minimal`
3. Side-by-side server: `dotnet run --project e:/GitHub/DocRAG/DocRAG.Mcp --launch-profile DevSideBySide --no-build -c Debug` (port 6101)
4. `rescrub_library aerotech-aeroscript 1.0` — expect populated `library_excluded_symbols`, hint fires.
5. `list_excluded_symbols aerotech-aeroscript 1.0 limit=20` — confirm residual noise (`RealTek`, `_QUAD_COPPER`, `AFuwU`, `along`, `data`, `enumerator`, `integer`) appears with reasons + samples.
6. `add_to_stoplist aerotech-aeroscript 1.0 ["along","data","enumerator","integer"]`.
7. `rescrub_library` again — those four show reason `LibraryStoplist`.
8. `list_enums aerotech-aeroscript` — noise enums gone.

## Files Touched

| File | Change |
|---|---|
| `DocRAG.Core/Enums/SymbolRejectionReason.cs` | New |
| `DocRAG.Core/Models/ExcludedSymbol.cs` | New |
| `DocRAG.Core/Models/LibraryProfile.cs` | Add `Stoplist` field; bump schema version |
| `DocRAG.Core/Interfaces/IExcludedSymbolsRepository.cs` | New |
| `DocRAG.Database/DocRagDbContext.cs` | Register `ExcludedSymbols` collection + indexes |
| `DocRAG.Database/Repositories/ExcludedSymbolsRepository.cs` | New |
| `DocRAG.Database/Repositories/RepositoryFactory.cs` | Add `GetExcludedSymbolsRepository(profile)` |
| `DocRAG.Database/ServiceCollectionExtensions.cs` | Register new repo |
| `DocRAG.Ingestion/Symbols/Stoplist.cs` | Add profile-aware `Match` + `StoplistMatch` enum |
| `DocRAG.Ingestion/Symbols/SymbolExtractor.cs` | Return rejection records; reason resolution order |
| `DocRAG.Ingestion/Symbols/RejectedToken.cs` | New |
| `DocRAG.Ingestion/Symbols/ExtractedSymbols.cs` | Add `Rejected` list |
| `DocRAG.Ingestion/Symbols/SampleWindowExtractor.cs` | New |
| `DocRAG.Ingestion/Recon/RejectionAccumulator.cs` | New |
| `DocRAG.Ingestion/Recon/RescrubService.cs` | Wire rejection capture + hint computation |
| `DocRAG.Ingestion/Recon/LibraryProfileService.cs` | Stoplist carry-forward on UpsertAsync |
| `DocRAG.Mcp/Tools/SymbolManagementTools.cs` | New (3 MCP tools) |
| `DocRAG.Mcp/Tools/RescrubTools.cs` | None (new fields ride on `RescrubResult`) |
| Test files | See Testing Strategy table above |

## Commit Plan

Four logical commits, each independently buildable + testable:

1. Schema + repository (enum, record, `LibraryProfile.Stoplist`, repo + factory + DI + indexes).
2. Extractor rejection capture (`StoplistMatch`, `RejectedToken`, extended `ExtractedSymbols`, reason-returning `IsAdmissible` rewrite, `SampleWindowExtractor`).
3. Rescrub plumbing + profile carry-forward (`RejectionAccumulator`, `RescrubService` wiring, hint computation, `LibraryProfileService` carry-forward).
4. MCP tools + tests + verification (`SymbolManagementTools`, full test suite, end-to-end check).

Estimate: 4–6 hours focused, comparable scope to the BM25 sharding overhaul.
