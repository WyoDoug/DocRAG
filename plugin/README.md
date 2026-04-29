# DocRAG plugin for Claude Code

A Claude Code plugin that wires DocRAG into Claude sessions:

- Registers the DocRAG MCP server. Tool eager-loading is driven by per-tool `[McpMeta("anthropic/alwaysLoad", true)]` flags on entry-point tools in the server source — the plugin's `.mcp.json` does not set server-wide `alwaysLoad`, so only the 6 entry-point tools occupy session-start context. This requires DocRAG.Mcp built from a version that includes those per-tool flags (see compatibility table below).
- Bundles a `docrag-first` skill that triggers on coding questions and tells Claude to query DocRAG before answering from training data

## Server compatibility

| DocRAG.Mcp version | Behavior with this plugin |
|---|---|
| Has per-tool `McpMeta` flags (post-Apr 2026) | 6 entry-point tools eager (~1–2k tokens). Optimal. |
| No per-tool flags (older builds) | Nothing eager. Discoverability falls back to ToolSearch. To force eager-loading on an older server, add `"alwaysLoad": true` to the server entry in this plugin's `.mcp.json` (loads all 33 tools, ~5–10k tokens). |

## Prerequisites

The plugin only handles Claude Code wiring. The DocRAG server itself, plus its dependencies, must be installed separately. DocRAG runs on Windows, macOS, and Linux — pick the install path for your platform.

### Common to all platforms

1. **MongoDB Community Edition** — port 27017. [Download](https://www.mongodb.com/try/download/community)
2. **Ollama** — port 11434. [Download](https://ollama.com)

### Windows (MSI installer — easiest)

Download `DocRAG.Mcp.msi` from the [latest release](https://github.com/WyoDoug/DocRAG/releases/latest) and run it. Installs the `DocRAGMcp` Windows service on `http://localhost:6100`.

Verify:

```powershell
Get-Service DocRAGMcp
Invoke-WebRequest http://localhost:6100/health
```

Full prereq install guide: see the [main DocRAG README](https://github.com/WyoDoug/DocRAG#quick-start-windows-installer).

### macOS / Linux / Windows (Docker — cross-OS unified)

A `docker-compose.yml` in the DocRAG repo brings up MongoDB, Ollama, and DocRAG.Mcp together. From the repo root:

```bash
docker compose up -d
```

This is the simplest cross-OS path — one command, identical behavior on every platform. Requires Docker Desktop (Win/Mac) or Docker Engine (Linux).

Verify:

```bash
curl http://localhost:6100/health
```

### macOS / Linux (.NET self-contained binaries)

If you don't want Docker, download the platform tarball from the [latest release](https://github.com/WyoDoug/DocRAG/releases/latest):

| Platform | Artifact |
|---|---|
| macOS Apple Silicon | `docrag-osx-arm64.tar.gz` |
| macOS Intel | `docrag-osx-x64.tar.gz` |
| Linux x64 | `docrag-linux-x64.tar.gz` |
| Linux ARM64 | `docrag-linux-arm64.tar.gz` |

Extract and run:

```bash
tar -xzf docrag-osx-arm64.tar.gz -C ~/docrag
~/docrag/DocRAG.Mcp
```

Or wrap as a `launchd` (macOS) or `systemd` (Linux) service. Sample unit files: see `packaging/` in the DocRAG repo.

### Build from source (any platform)

```bash
git clone https://github.com/WyoDoug/DocRAG.git
cd DocRAG
dotnet run --project DocRAG.Mcp
```

Requires .NET SDK 10.0+ on any platform.

## Install the plugin

### Local development

```bash
claude --plugin-dir <path-to-this-plugin>
```

For example, if you've cloned the DocRAG repo:

```bash
claude --plugin-dir E:/GitHub/DocRAG/plugin
```

### From git (once published)

```bash
claude plugin install https://github.com/WyoDoug/DocRAG --plugin-dir plugin
```

(Exact syntax depends on whether the plugin is in a sub-path of the repo or its own repo. If the plugin is later split out into `wyodoug/docrag-plugin`, drop the `--plugin-dir`.)

### Marketplace

Not yet published. Submit at [platform.claude.com/plugins/submit](https://platform.claude.com/plugins/submit) when ready.

### Scope

By default, plugins install at user scope (`~/.claude/settings.json`). To install per-project (shared with team via git):

```bash
claude plugin install <source> --scope project
```

## What the plugin does at session start

1. **MCP server is registered** as `docrag` pointing at `http://localhost:6100/mcp`. The 6 entry-point tools tagged with `[McpMeta("anthropic/alwaysLoad", true)]` in DocRAG.Mcp source land in the system prompt at session start; the other ~27 admin tools stay deferred behind ToolSearch.
2. **Skill `docrag:docrag-first` loads eagerly.** Its description triggers on coding questions — Claude reads it at session start and applies the protocol on the first matching task.

Claude will, on a coding question that names an indexed library:
- Recognize the skill's trigger conditions
- Run `mcp__docrag__list_libraries` to find a match
- Query `mcp__docrag__search_docs` or `mcp__docrag__get_class_reference` before answering

## Eager tool set

The 6 tools that load eagerly into every session (~1–2k tokens of context overhead). The flag is set at the source level in DocRAG.Mcp via `[McpMeta("anthropic/alwaysLoad", true)]` — to add or remove a tool from the eager set, edit the attribute on the corresponding tool method.

| Tool | File | Why eager |
|---|---|---|
| `get_dashboard_index` | `HealthTools.cs` | Designed as the "Start here" cue for fresh sessions |
| `list_libraries` | `LibraryTools.cs` | First call in the docrag-first protocol |
| `search_docs` | `SearchTools.cs` | Primary natural-language query |
| `get_class_reference` | `SearchTools.cs` | Symbol/class lookup |
| `get_library_overview` | `SearchTools.cs` | Library orientation |
| `list_symbols` | `LibraryTools.cs` | Library exploration |

Everything else (ingestion, scrape jobs, library admin, profile management, log retrieval, page operations, suspect/health diagnostics) is deferred — it's only relevant when actively managing the index, not during normal coding sessions, and Claude can pull schemas via ToolSearch if needed.

## Distribution

The plugin is OS-agnostic — its only payload is JSON and Markdown. Distribution paths:

### Local install for development

```bash
claude --plugin-dir <path-to-DocRAG-repo>/plugin
```

### Git URL install

Once the plugin source is on a public branch:

```bash
claude plugin install https://github.com/WyoDoug/DocRAG --plugin-dir plugin
```

If the plugin is later split into its own repo (e.g., `WyoDoug/docrag-plugin`), drop the `--plugin-dir` flag.

### Marketplace submission

The Claude Code plugin marketplace at [platform.claude.com/plugins/submit](https://platform.claude.com/plugins/submit) accepts plugin submissions. To submit:

1. Push the plugin source to a public GitHub repo (either inside the main DocRAG repo as `plugin/`, or a sibling repo).
2. Tag a release with semantic versioning (`v0.1.0`, `v0.2.0`, …) so consumers can pin.
3. Fill out the submission form. You will need: plugin name, repository URL, license, screenshots / description, support contact.
4. After review, the plugin appears in the marketplace and users install via `claude plugin install docrag@anthropic-marketplace` (or whichever marketplace it lands in).

### Cross-OS considerations for the plugin itself

The plugin works on every platform Claude Code supports (Windows, macOS, Linux) without changes. The only platform-specific concern is the **DocRAG server** the plugin points to — see Prerequisites above for the per-platform install paths. The plugin's `.mcp.json` uses `http://localhost:6100/mcp`, which is platform-neutral.

## Troubleshooting

**Tools not visible at session start.** Check Claude Code version — `alwaysLoad` requires v2.1.121+. Run `claude --version`.

**"Connection refused" when calling DocRAG tools.** The Windows service isn't running. `Get-Service DocRAGMcp` and `Start-Service DocRAGMcp` if Stopped. Check `%ProgramData%\DocRAG\logs\` for startup errors.

**Slow first query after a long idle.** The DocRAG server warms its in-memory vector index on first request. Pre-warm by visiting `http://localhost:6100/health` after service start, or invoke `DocRAG.Mcp.exe --prewarm` from the install directory.

**Old `.mcp.json` in the project root conflicts with this plugin.** The plugin registers `docrag` as an MCP server. If the project also has a top-level `.mcp.json` registering the same server name, you may end up with double registration or a precedence issue. Either remove the project `.mcp.json` once the plugin is in place, or set the project's `disabledMcpjsonServers: ["docrag"]` so only the plugin's registration is active.

**Stale `NODE_EXTRA_CA_CERTS` setting.** Earlier DocRAG setups used a localhost TLS cert. The current MSI ships with plain HTTP. If `~/.claude/settings.json` has `env.NODE_EXTRA_CA_CERTS` pointing at a `localhost.pem` file you don't use anymore, it's safe to remove.

## Updating

```bash
claude plugin update docrag
```

Or, for `--plugin-dir` installs, just `git pull` in the source directory.

## Uninstall

```bash
claude plugin uninstall docrag
```

This removes the plugin's MCP registration and skill. The DocRAG MSI / Windows service are not affected — uninstall those separately via Windows "Add or remove programs" if desired.
