# MCP Tool UX Cleanup — Design Spec

**Date:** 2026-04-27
**Status:** Draft
**Scope:** Make the SaddleRAG MCP tool surface usable by a fresh LLM consumer — fix cold-start dead ends, add visibility into library health, expose rename/delete/cancel, consolidate the two scrape tools, surface "this URL probably isn't docs" via the existing recon delegation pattern, and collapse redundant listing/resume tools.

---

## Problem

A second-pass LLM review of the current tool surface (after using SaddleRAG against a populated database, then mentally walking through a fresh-database flow) flagged a set of friction points:

1. **No cold-start orientation.** `list_libraries` returns `[]` on an empty DB and there is no documented "start here" tool. The LLM has to guess between `start_ingest`, `scrape_docs`, `index_project_dependencies`, etc.
2. **No way to detect a misindexed library.** A library can be 100% the wrong content (e.g., `mongodb.driver` indexed as Go/Ruby docs because `PackageProjectUrl` resolved to a multi-driver landing page) and the only way to discover it is to read chunks one-by-one.
3. **No mutating operations.** When a library is misnamed or the index is unrecoverable, the LLM can either live with it or leave an orphan record.
4. **`scrape_docs` and `scrape_library` overlap unclearly.** Both queue, both return `JobId`. The actual difference (cache-aware + auto-derived patterns vs explicit pattern control) is not visible from the descriptions.
5. **Bad source URLs go uncaught.** `index_project_dependencies` follows `PackageProjectUrl` blindly; if it lands on a README or a wrong host, the resulting "scrape" is a one-pager indexed under the package name.
6. **`BoundaryIssues` count in `rescrub_library` output isn't actionable.** The LLM sees `BoundaryIssues: 6` and has no threshold to compare against.

The goal of this branch is to fix all six in one focused tool-surface pass without touching the streaming pipeline, the recon-from-scraped-pages idea, or job polling (deferred to later specs).

---

## Solution

Eight new tools, six removed tools (one replaces four; two collapse via flag/removal), four tools with modified behavior, one ingestion-side detector, two new `LibraryVersion` fields, two new `ScrapeJob` fields. Net change to the MCP surface is **+2 tools**. The work splits into seven tracks that share data model touch-points but are otherwise independent and can be implemented in parallel.

### Track A — Cold-start entry points

**New tool: `get_dashboard_index`**

Single-call orientation tool. Returns:

```
LibraryCount, VersionCount, RecentJobs[≤5], StaleCount, SuspectCount,
StaleLibraries[≤20] { library, version, reasons[] },
SuspectLibraries[≤20] { library, version, reasons[] },
SuggestedNextAction { tool, args, message }
```

`SuggestedNextAction` is computed:
- `LibraryCount == 0` → `index_project_dependencies` or `scrape_docs`
- `SuspectCount > 0` → `submit_url_correction` for the first suspect
- `StaleCount > 0` → `rescrub_library` for the first stale entry
- otherwise → `null` (caller picks)

`SuspectLibraries` and `StaleLibraries` are capped at 20; if more exist, a `Truncated: true` flag is set with the total count.

**Modified: `list_libraries`** — when the result is empty, return:

```json
{
  "Libraries": [],
  "Hint": "Database is empty. Call get_dashboard_index for orientation, or use index_project_dependencies(path=...) / scrape_docs(url=..., libraryId=..., version=...) to ingest."
}
```

When non-empty, return as today (just the array).

### Track B — Library visibility

**New tool: `get_library_health`**

Per-version diagnostic snapshot. Distinct from `get_library_overview` (which returns docs content):

```
Identity:        library, version, currentVersion, lastScrapedAt, parserVersion
Scale:           chunkCount, pageCount, distinctHostCount, hostnames[≤20]
Content mix:     languageMix { csharp: 0.80, go: 0.05, ... },
                 declaredLanguages[]   (from LibraryProfile)
Quality:         boundaryIssuePct, boundaryIssueCount,
                 staleChunkCount, parserVersionMatch (bool)
Suspect:         suspect (bool), suspectReasons[]
                 (OnePager | SparseLinkGraph | SingleHost | LanguageMismatch | ReadmeOnly)
SuggestedAction: { tool, args, message } | null
```

`languageMix` is computed on-the-fly from chunk code-fence metadata; values are fractions in `[0, 1]` summing to ≤ 1.0 (chunks with no code fences contribute to `unfenced`). If `declaredLanguages` is non-empty and the share of any declared language is below `0.30` of `languageMix`, `LanguageMismatch` is added to `suspectReasons`. Threshold lives as a tunable constant. `boundaryIssuePct` is a percent in `[0, 100]` to match its name.

`SuggestedAction` is computed:
- `suspect == true` → `submit_url_correction`
- `boundaryIssuePct >= 10%` → `rechunk_library`
- `parserVersionMatch == false` → `rescrub_library`
- otherwise → `null`

### Track C — Mutating operations

All three use `dryRun=true` as default.

**`rename_library(library, newId, dryRun=true)`**

Hard rename. Updates the `LibraryId` field across `Libraries`, `LibraryVersions`, `Chunks`, `LibraryProfiles`, `LibraryIndexes`, `Bm25Shards`, `ExcludedSymbols`, `Pages`, `ScrapeJobs`. `dryRun` returns the row count per collection. Pre-check: if `newId` already exists in `Libraries`, error out with `Status: Collision`. No silent merge.

**`delete_library(library, dryRun=true)`**

Hard delete across `Libraries`, `LibraryVersions`, `Chunks`, `LibraryProfiles`, `LibraryIndexes`, `Bm25Shards`, `ExcludedSymbols`, `Pages`. `ScrapeJobs` is *retained* (audit trail). `dryRun` returns:

```
WouldDelete {
  versions: ["1.0", "2.0"],
  chunks: N, pages: M, profiles: K, indexes: K,
  bm25Shards: K, excludedSymbols: K, rawPages: K
}
ScrapeJobsRetained: J
```

**`delete_version(library, version, dryRun=true)`**

Same cascade as `delete_library` but for one version. Edge cases:
- If the deleted version is the only version → cascade-deletes the `Library` row too.
- If the deleted version is `currentVersion` and other versions exist → repoints `currentVersion` to the next-most-recent version (by `LastScrapedAt`).

Both behaviors are reported in `dryRun` output as `LibraryRowAffected` and `CurrentVersionRepointedTo`.

### Track D — Scrape tool consolidation

**Modified: `scrape_docs`** gains optional `allowedUrlPatterns: string[]` and `excludedUrlPatterns: string[]`. When omitted, behavior is unchanged (auto-derived from URL host). Cache-aware behavior (returns `AlreadyCached` unless `force=true`) is unchanged.

**Removed: `scrape_library`**. Description on `scrape_docs` is sharpened to make the role obvious:

> "Scrape documentation from a URL. Cache-aware — returns AlreadyCached unless force=true. Pass `allowedUrlPatterns` / `excludedUrlPatterns` only if the auto-derived host filter is too narrow or too broad. Use this for both ad-hoc URLs and post-recon scrapes — there is no separate 'scrape_library' tool."

**Modified: `start_ingest`** READY_TO_SCRAPE response points at `scrape_docs` (was `scrape_library`).

`scrape_library` is removed outright (no deprecation alias). All callers in this repo are updated. External callers — none, this is single-user — are not a concern.

### Track E — URL sanity via recon delegation

**New ingestion-side detector.** When the streaming pipeline finishes, before final indexing, evaluate the per-version result against suspect heuristics:

- `OnePager` — total page count ≤ 3
- `SparseLinkGraph` — fewer than 10 unique outbound link targets across all crawled pages
- `SingleHost` — only one hostname seen (when the LibraryProfile declared the docs span multiple subdomains)
- `LanguageMismatch` — share of any declared `LibraryProfile.languages` entry is below `0.30` of `languageMix`
- `ReadmeOnly` — root URL host is `github.com` and only README pages were indexed

Multiple reasons can apply. If any reason fires, `LibraryVersion.Suspect = true` and `LibraryVersion.SuspectReasons[]` is set.

**New tool: `submit_url_correction(library, version, newUrl, dryRun=false)`**

Recon-style callback. Drops the suspect chunks, pages, profile, indexes, and bm25 shards (reusing Track C's per-version cascade), clears `LibraryVersion.Suspect` and `SuspectReasons`, then queues `scrape_docs` rooted at `newUrl`. Returns the new `JobId`. `dryRun=true` reports what would be deleted before re-queuing without actually deleting or queuing.

**Modified: `start_ingest`** adds two new states:

- `URL_SUSPECT` — `LibraryVersion.Suspect == true`. Returns `suspectReasons`, `pageCount`, `hostnames[]`, sample page titles, and `submit_url_correction` as `nextTool`. Calling LLM is asked to browse the URL itself and either confirm or supply a corrected one.
- `IN_PROGRESS` — an active `ScrapeJob` exists for this `(library, version)`. Returns `jobId` and points at `get_scrape_status`. Prevents the LLM from queuing duplicate work.

State precedence (most-specific first): `IN_PROGRESS` > `URL_SUSPECT` > existing states (`RECON_NEEDED` / `READY_TO_SCRAPE` / `STALE` / `READY`).

**Resume refusal on suspect libraries** — `scrape_docs(resume=true)` (the consolidated successor to `continue_scrape`, see Track G) refuses when the target library is `Suspect=true`. Returns `Status: Refused` with a hint pointing at `submit_url_correction`. The LLM can override by calling `submit_url_correction` first (which clears the flag and re-queues with a corrected URL).

**Modified: `rescrub_library`** output includes a `BoundaryHint` field:

```
boundaryIssueCount, boundaryIssuePct,
hint: null | "rechunk_library may help"      (5% ≤ pct < 10%)
    | "rechunk_library recommended"          (pct >= 10%)
```

The same hint logic surfaces inside `get_library_health.SuggestedAction`.

### Track F — Job cancellation

The Apr 14 orphan job (`Running` for two weeks with no progress) and the in-flight `mongodb.driver` 26K-page runaway scrape are concrete evidence that "stop a scrape" is a real need, not a future luxury.

**New tool: `cancel_scrape(jobId)`**

Marks a `ScrapeJob` as `Cancelled`. Behavior depends on job state:

- **`Running` with active runner** — signals the pipeline `CancellationTokenSource`. Stages drain naturally; job moves to `Cancelled` once the pipeline observes the cancellation.
- **`Running` orphaned** — runner has no `CancellationTokenSource` registered for this jobId (process restarted while job was active). Updates the DB row to `Cancelled` directly with no signal.
- **`Completed` / `Failed` / `Cancelled`** — returns `Status: AlreadyTerminal` with a note. No-op.
- **Job not found** — returns `Status: NotFound`.

Partial results (chunks/pages already ingested before cancellation) are **kept**. To clear them, the caller uses `delete_version` after cancellation (or `submit_url_correction` if the cancel was triggered by a wrong URL — that tool clears partial results as part of its existing flow).

**`ScrapeJobRunner` change.** Maintain `IDictionary<string, CancellationTokenSource>` keyed by `jobId` for active jobs. Add `CancelAsync(string jobId, CancellationToken ct)` that signals the registered CTS or, if absent, marks the DB row directly. Entries are removed when the job completes (CTS disposed).

**Modified: `start_ingest`** — `IN_PROGRESS` state response includes `cancel_scrape` as an alternative `nextTool` alongside `get_scrape_status`. Calling LLM picks: poll, or cancel and start over.

**Modified: `get_dashboard_index`** — entries in `RecentJobs` carry a `Stale: true` flag for `Running` jobs whose `LastProgressAt` is older than 4 hours. Detector: any forward motion in `PagesFetched` / `PagesCompleted` / `ChunksGenerated` / `ChunksEmbedded` updates `LastProgressAt`. The dashboard's `SuggestedNextAction` for stale-running jobs is `cancel_scrape`.

**`ScrapeJob` data model.** Two new fields:

```
LastProgressAt: DateTime?      (updated when any pipeline counter increments)
CancelledAt: DateTime?         (set when transitioning to Cancelled)
```

Plus a new enum value: `ScrapeJobStatus.Cancelled = 4` and matching `PipelineState = "Cancelled"`.

### Track G — Surface consolidation

Two redundant tool clusters are folded down. No data-model changes.

**G.1 — Fold `continue_scrape` into `scrape_docs(resume=true)`**

`scrape_docs` gains:

```
resume: bool = false
```

When `resume=true`:
- `url` becomes optional. If omitted, the system finds the most recent `ScrapeJob` for `(libraryId, version)` and reuses its `RootUrl`, `AllowedUrlPatterns`, and `ExcludedUrlPatterns`.
- If the caller supplies any of those four args, the supplied value overrides the previous job's value. Useful for "resume but with a wider crawl."
- The cache check still runs — fully indexed, no `force` → returns `AlreadyCached` (resume becomes a no-op).
- If no prior job exists for `(libraryId, version)`, returns `Status: NoPriorJob` with a hint pointing at fresh `scrape_docs(url=…)`.
- If the library is `Suspect=true`, returns `Status: Refused` with a hint pointing at `submit_url_correction`. (Same refusal that Track E originally specified for `continue_scrape`.)

When `resume=false` (default): behavior is current `scrape_docs` behavior.

**Removed: `continue_scrape`.** Track E's `continue_scrape` modifications (refuse on suspect) move to `scrape_docs(resume=true)` instead.

**G.2 — Collapse `list_classes` / `list_enums` / `list_functions` / `list_parameters` into `list_symbols`**

**New tool: `list_symbols(library, kind?, filter?, version?, profile?)`**

```
library:  string                                         (required)
kind:     "class" | "enum" | "function" | "parameter"?   (optional; null = all kinds)
filter:   string?                                        (optional partial-name filter)
version:  string?                                        (defaults to current)
profile:  string?
```

Return shape: `[{ name: string, kind: "class"|"enum"|"function"|"parameter" }]`. Always structured (even when `kind` is specified) so the LLM doesn't need different parsing paths for the two cases.

The four removed tools shared an internal helper (`ListSymbolsByKindAsync`); `list_symbols` becomes that helper's only public entry point.

**Removed: `list_classes`, `list_enums`, `list_functions`, `list_parameters`.** Updates needed in:
- `LibraryTools.cs` — replace four `[McpServerTool]` methods with one
- Any internal callers (search/dashboard/health response builders) — switch to direct repository calls instead of the public tool

**Net effect of Track G:** removes five tools, adds one. Combined with the rest of the spec: surface goes from ~30 → ~32 instead of ~36, and the symbol-listing surface gets a single, parameter-discriminated entry instead of four-near-duplicates.

---

## Data Model Changes

### `LibraryVersion`

Add three fields:

```
Suspect: bool                       (default false)
SuspectReasons: string[]            (default empty)
LastSuspectEvaluatedAt: DateTime?   (nullable)
```

`Suspect` is set by the post-scrape detector at ingestion time and cleared by `submit_url_correction` (which also drops the suspect data).

### `Chunk` / repository-level

No schema changes. `boundaryIssuePct` is computed from existing `Chunk.BoundaryIssue` flags (already populated by the rescrub flow). `languageMix` is computed from existing `Chunk.CodeFenceLanguages[]` (or the equivalent — to be confirmed in the plan).

### Repository methods (new)

- `ILibraryRepository.RenameAsync(string oldId, string newId, CancellationToken)` — atomic rename.
- `ILibraryRepository.DeleteAsync(string library, CancellationToken)` — full cascade.
- `ILibraryVersionRepository.DeleteVersionAsync(string library, string version, CancellationToken)` — single-version cascade.
- `ILibraryVersionRepository.SetSuspectAsync(string library, string version, string[] reasons, CancellationToken)`.
- `ILibraryVersionRepository.ClearSuspectAsync(string library, string version, CancellationToken)`.
- `IChunkRepository.GetLanguageMixAsync(string library, string version, CancellationToken)`.
- `IChunkRepository.GetBoundaryIssueStatsAsync(string library, string version, CancellationToken)`.
- `IChunkRepository.GetHostnameDistributionAsync(string library, string version, CancellationToken)`.
- `IChunkRepository.GetSampleTitlesAsync(string library, string version, int limit, CancellationToken)` — for `URL_SUSPECT` payload.

Cascade methods are implemented as a sequence of single-collection operations rather than a Mongo transaction (single-user system, no concurrent mutators). Order is: `Chunks` → `LibraryIndexes` → `Bm25Shards` → `ExcludedSymbols` → `LibraryProfiles` → `Pages` → `LibraryVersions` → `Libraries`. If a step fails partway, the operation is left in a partial state; the caller can re-run `delete_library` to finish (idempotent — missing rows in early collections are no-ops).

---

## Canonical Session Flows

### Flow A — Fresh database

```
LLM:    get_dashboard_index
Server: { LibraryCount: 0, SuggestedNextAction: { tool: "index_project_dependencies", ... } }
LLM:    index_project_dependencies(path="C:/Repos/MyApp")
Server: { ScrapesQueued: 5, ... }
LLM:    get_dashboard_index
Server: { LibraryCount: 5, SuspectCount: 1, SuspectLibraries: [{ library: "mongodb.driver", reasons: ["LanguageMismatch", "OnePager"] }], ... }
LLM:    start_ingest(library="mongodb.driver", version="3.5.0", url="...")
Server: { Status: "URL_SUSPECT", reasons: [...], NextTool: "submit_url_correction", ... }
LLM:    [browses, finds correct URL]
LLM:    submit_url_correction(library="mongodb.driver", version="3.5.0", newUrl="https://mongodb.github.io/mongo-csharp-driver/")
Server: { JobId: "...", Status: "Queued" }
```

### Flow B — Misnamed library

```
LLM:    list_libraries
Server: [..., { library: "aerotech-aeroscript", currentVersion: "..." }]
LLM:    rename_library(library="aerotech-aeroscript", newId="aerotech-aero-script", dryRun=true)
Server: { WouldRename: { libraries: 1, chunks: 1240, ... } }
LLM:    rename_library(library="aerotech-aeroscript", newId="aerotech-aero-script", dryRun=false)
Server: { Renamed: { ... } }
```

### Flow C — Stale parser version

```
LLM:    get_dashboard_index
Server: { StaleCount: 2, StaleLibraries: [{ library: "questpdf", version: "2024.5", reasons: ["ParserVersionDrift"] }, ...] }
LLM:    rescrub_library(library="questpdf", version="2024.5")
Server: { ChunksProcessed: 800, BoundaryIssueCount: 41, BoundaryIssuePct: 5.1, BoundaryHint: "rechunk_library may help" }
```

---

## Implementation Order

The seven tracks share data-model surface but are otherwise independent. Suggested order for the plan:

1. **Track C — Mutations.** Smallest, most isolated. Repository cascade methods + three tools. Lays the groundwork for `submit_url_correction` and `cancel_scrape`'s partial-result cleanup paths to reuse the cascade plumbing.
2. **Track F — Cancellation.** `ScrapeJobRunner` CTS registry + `cancel_scrape` tool + `LastProgressAt` tracking + `Cancelled` enum value. Independent of Tracks B/E, but its `IN_PROGRESS`-state addition to `start_ingest` will be picked up by Track E. Lands early so the runaway `mongodb.driver` job can be killed before further integration testing.
3. **Track G — Surface consolidation.** Pure tool-merge work, no data-model touch. Lands before D so Track D can include `resume=true` plumbing in `scrape_docs` from the start. Splits in two: G.1 (`continue_scrape` → `scrape_docs(resume=true)`) and G.2 (`list_*` → `list_symbols`). Either half can land independently.
4. **Track D — Scrape consolidation.** Touches one tool, one description, one `start_ingest` arg. Fast win, no data-model change.
5. **Track B — `get_library_health`.** New repo aggregation methods + one tool. Independent of suspect detection (returns `Suspect: false` for everything until Track E lands).
6. **Track E — URL sanity.** Detector hook in the ingestion pipeline + new state + new tool + `LibraryVersion.Suspect` field. Largest surface but builds on Track C's cascade methods.
7. **Track A — Cold start.** `get_dashboard_index` aggregates Tracks B, E, and F (stale-running detection), so it lands last. `list_libraries` empty hint is a one-line change anywhere in the order.

Each track ends with at least one integration test covering the canonical session flow above.

---

## Out of Scope (explicit)

- **Pre-scrape URL prober.** Decided to lean on recon delegation instead. The calling LLM browses on `URL_SUSPECT`; we don't maintain a doc-host allowlist.
- **`merge_libraries` tool.** Only useful if rename collisions become common. Defer.
- **Audit log of mutations.** `ScrapeJobs` retained on delete is sufficient for now.
- **Recon-from-scraped-pages mode.** Saved for a separate spec.
- **Job polling / wait-for-job.** Acknowledged out of scope by original feedback.
- **Soft delete.** Decided against — adds zombie state to every read.
- **Auto-cancel of stale-running jobs.** `get_dashboard_index` flags them; the LLM (or user) decides. We don't auto-cancel based on `LastProgressAt` age — too easy to kill a legitimately slow scrape.

---

## Risks

- **Suspect detection false positives.** A legitimately small docs site (3 pages, single host) may trip `OnePager` + `SingleHost`. Mitigation: detector flags but doesn't auto-delete; the LLM can confirm via `submit_url_correction` with the same URL (which clears the flag and re-runs without the suspect bit). If false-positive rate is too high in practice, raise the page-count threshold or require ≥ 2 reasons.
- **Cascade order under failure.** If a delete cascade fails partway (e.g., DB connection lost), the library is in a partial state. Mitigation: idempotent re-run, plus the cascade order leaves `Libraries`/`LibraryVersions` rows for last so the entry remains visible until everything else is gone.
- **`languageMix` performance.** Computing on-the-fly across all chunks could be slow for huge libraries. Mitigation: `IChunkRepository.GetLanguageMixAsync` runs as a Mongo aggregation with `$group`, not a client-side scan. If still slow, persist as a `LibraryVersion.LanguageMix` field updated by the post-scrape detector.
- **`get_library_health` vs `get_library_overview` confusion.** Names share a prefix. Mitigation: both descriptions explicitly contrast. `health` description: "Returns diagnostic state (chunk count, language mix, parser drift, suspect markers). For the actual library content, use get_library_overview." `overview` description gets a similar reciprocal note.
- **Cancel-vs-complete race.** `cancel_scrape` could be called the instant a job is completing. The CTS is about to be disposed; the DB row is about to flip to `Completed`. Mitigation: `CancelAsync` re-reads the job row inside the cancel path; if `Status` is already terminal, returns `AlreadyTerminal` without signalling. Worst case is a benign no-op.
- **CTS registry leak.** If `ScrapeJobRunner` forgets to remove a `CancellationTokenSource` entry on completion, memory grows. Mitigation: registration uses `try/finally` around the pipeline `await Task.WhenAll(...)`; finally block disposes and removes the entry whether the pipeline completed normally, threw, or was cancelled.
