# DocRAG

**Documentation Retrieval-Augmented Generation for AI coding assistants.**

DocRAG scrapes documentation websites, classifies and chunks the content with a local LLM, generates vector embeddings, and stores everything in MongoDB. It exposes the indexed documentation through MCP (Model Context Protocol) tools so that AI assistants like Claude Code, GitHub Copilot, and others can search your documentation library in real time.

## Why DocRAG?

AI coding assistants are limited by their training cutoff and context window. When you're working with a niche library, a new release, or internal documentation, the assistant doesn't know about it. DocRAG bridges that gap:

- **Scrape any documentation site** into a searchable vector database
- **Auto-index project dependencies** from NuGet, npm, and pip
- **Serve documentation to your AI assistant** via MCP tools during coding sessions
- **Track multiple versions** of the same library and diff changes between them
- **Share a company-wide documentation database** across your team

## Architecture

```
Documentation Sites          DocRAG Pipeline                    AI Assistants
==================          ===============                    ==============

docs.example.com  ──┐
                    │      ┌─────────────┐
github.com/repo   ──┼──►   │  Playwright  │  (headless browser)
                    │      │   Crawler    │
learn.microsoft   ──┘      └──────┬──────┘
                                  │
                           ┌──────▼──────┐
                           │   Ollama    │  (local LLM)
                           │  Classifier │  qwen3:1.7b
                           └──────┬──────┘
                                  │
                           ┌──────▼──────┐
                           │  Category-  │
                           │   Aware     │
                           │  Chunker    │
                           └──────┬──────┘
                                  │
                           ┌──────▼──────┐
                           │   Ollama    │  nomic-embed-text
                           │  Embedder   │  (768 dimensions)
                           └──────┬──────┘
                                  │
                           ┌──────▼──────┐     ┌──────────────┐
                           │   MongoDB   │◄───►│  MCP Server  │──► Claude Code
                           │  (storage)  │     │  (SSE/HTTP)  │──► Copilot
                           └─────────────┘     └──────────────┘──► Any MCP client
```

## Prerequisites

| Dependency | Version | Purpose |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0+ | Build and run |
| [MongoDB](https://www.mongodb.com/try/download/community) | 6.0+ | Document storage and indexing |
| [Ollama](https://ollama.ai) | Latest | Local LLM for embeddings and classification |

Ollama models are pulled automatically on first run:
- `nomic-embed-text` (embedding generation, 768 dimensions)
- `qwen3:1.7b` (page classification and optional re-ranking)

## Quick Start

### 1. Clone and Build

```bash
git clone https://github.com/JackalopeTechnologies/DocRAG.git
cd DocRAG
dotnet build DocRAG.slnx
```

### 2. Start MongoDB

```bash
# Docker (easiest)
docker run -d -p 27017:27017 --name docrag-mongo mongo:latest

# Or install locally: https://www.mongodb.com/docs/manual/installation/
```

### 3. Start Ollama

```bash
# Install from https://ollama.ai, then:
ollama serve
# Models are pulled automatically on first use
```

### 4. Start the MCP Server

```bash
dotnet run --project DocRAG.Mcp
```

The server starts on `http://localhost:6100` by default.

### 5. Connect Your AI Assistant

Add to your MCP client configuration (e.g., `.mcp.json` in your project root):

```json
{
  "mcpServers": {
    "docrag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 60
    }
  }
}
```

For **Claude Code**, place this file in your project root or home directory. For other MCP clients, consult their documentation for server configuration.

### 6. Publish for Windows Service (`Release|x64`)

```bash
dotnet publish DocRAG.Mcp/DocRAG.Mcp.csproj -c Release -p:Platform=x64
```

The published output is configured for `win-x64`, includes Windows service hosting support, and uses the single MCP HTTP endpoint at `http://localhost:6100/mcp` by default. Register the published executable as the Windows service target rather than using `dotnet run` or `dotnet build` output.

### 7. Install as a Windows Service

For the simplest one-command flow, publish directly into the install directory and recreate the service automatically:

```powershell
.\DocRAG.Mcp\publish-and-install-service.ps1 -InstallDirectory 'E:\Service\DocRAG.Mcp'
```

This script publishes `DocRAG.Mcp.csproj` in `Release|x64`, installs or refreshes the `DocRAGMcp` service, and starts it unless you pass `-SkipStart`.

From an elevated PowerShell session, run either of these:

```powershell
.\DocRAG.Mcp\install-service.ps1
```

This copies the published output to `%ProgramFiles%\DocRAG\DocRAG.Mcp` and installs the `DocRAGMcp` service.

```powershell
.\DocRAG.Mcp\install-service.ps1 -InPlace
```

This installs the service directly from `DocRAG.Mcp\bin\x64\Release\net10.0\win-x64\publish`. If you use `-InPlace`, point the service at the `publish` directory under `bin`, not the generic `bin` folder.

To deploy an updated publish later:

```powershell
.\DocRAG.Mcp\update-service.ps1
```

To remove the service:

```powershell
.\DocRAG.Mcp\uninstall-service.ps1
```

## MCP Tools Reference

DocRAG exposes 16 tools through the MCP protocol. Your AI assistant discovers these automatically once connected.

### Search

| Tool | Description |
|---|---|
| `search_docs` | Natural language search across all libraries or filtered by library, version, and category (Overview, HowTo, Sample, ApiReference, ChangeLog) |
| `get_class_reference` | Look up API reference for a class or type by name. Searches across all libraries if none specified. Tries exact match, then fuzzy. |
| `get_library_overview` | Get Overview-category chunks for a library — concepts, architecture, getting started guides |

### Library Management

| Tool | Description |
|---|---|
| `list_libraries` | List all indexed libraries with current version and all ingested versions |
| `list_classes` | List all documented classes/types for a library, with optional name filter |

### Ingestion

| Tool | Description |
|---|---|
| `scrape_docs` | Scrape a documentation URL with auto-derived crawl settings. Cache-aware — skips already-indexed libraries unless `force=true` |
| `scrape_library` | Queue a scrape job with full control over URL patterns, depth, and delay. Returns a job ID for polling. |
| `dryrun_scrape` | Test a scrape configuration without writing to the database. Reports page counts, depth distribution, and GitHub repos that would be cloned. |
| `continue_scrape` | Resume an interrupted or MaxPages-limited scrape from where it left off |
| `get_scrape_status` | Poll a scrape job's progress by job ID |
| `list_scrape_jobs` | List recent scrape jobs with status |
| `index_project_dependencies` | Scan a project's NuGet/npm/pip dependencies and auto-index their documentation |

### Version Management

| Tool | Description |
|---|---|
| `get_version_changes` | Diff two versions of a library — added, removed, and changed pages with summaries |

### Configuration

| Tool | Description |
|---|---|
| `list_profiles` | List configured MongoDB database profiles |
| `reload_profile` | Reload the in-memory vector index from MongoDB (useful after manual data changes) |

### Diagnostics

| Tool | Description |
|---|---|
| `get_server_logs` | Retrieve recent server log lines, with optional text filter |

## CLI Tool

The CLI provides direct access to ingestion and management without the MCP server.

```bash
# Build the CLI
dotnet build DocRAG.Cli/DocRAG.Cli.csproj
```

### Commands

**Ingest a documentation library:**
```bash
docrag ingest \
  --root-url https://docs.example.com/ \
  --library-id example-lib \
  --version 2.0 \
  --hint "Example library for building widgets" \
  --allowed "docs.example.com" \
  --max-pages 500 \
  --delay 1000
```

**Dry-run a scrape (no database writes):**
```bash
docrag dryrun \
  --root-url https://docs.example.com/ \
  --allowed "docs.example.com" \
  --max-pages 200
```

**Inspect a page's link/sidebar structure (useful for tuning URL patterns):**
```bash
docrag inspect --url https://docs.example.com/getting-started
```

**List indexed libraries:**
```bash
docrag list
```

**Show ingestion status:**
```bash
docrag status --library-id example-lib
```

**Re-classify pages with the LLM (fix unclassified pages):**
```bash
docrag reclassify --library-id example-lib
docrag reclassify --all  # Reclassify everything, even already-classified pages
```

**Scan project dependencies and auto-index:**
```bash
docrag scan --path ./MyProject.sln
docrag scan --path ./package.json --profile company
```

**Manage database profiles:**
```bash
docrag profile list
```

## Configuration

### MongoDB Profiles

DocRAG supports multiple MongoDB databases via named profiles. Configure them in `appsettings.json`:

```json
{
  "MongoDB": {
    "ActiveProfile": "local",
    "Profiles": {
      "local": {
        "ConnectionString": "mongodb://localhost:27017",
        "DatabaseName": "DocRAG",
        "Description": "Local development database"
      },
      "company": {
        "ConnectionString": "mongodb://docrag.internal.company.com:27017",
        "DatabaseName": "DocRAG",
        "Description": "Shared company documentation database"
      }
    }
  }
}
```

Every MCP tool accepts an optional `profile` parameter to target a specific database. This enables scenarios like:
- Personal local index for experiments
- Shared team database with pre-indexed company libraries
- CI/CD pipeline that indexes docs on release

### Ollama Settings

```json
{
  "Ollama": {
    "Endpoint": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "EmbeddingDimensions": 768,
    "ClassificationModel": "qwen3:1.7b",
    "ReRankingModel": "qwen3:1.7b",
    "ModelPullTimeoutSeconds": 600
  }
}
```

### Environment Variables

All settings can be overridden via environment variables prefixed with `DOCRAG_`:

```bash
DOCRAG_MONGODB_PROFILE=company          # Override active profile
ASPNETCORE_ENVIRONMENT=Development      # Enable dev settings (disables re-ranking)
```

## Project Structure

```
DocRAG.slnx                    # Solution file
DocRAG.Core/                   # Domain models, interfaces, enums
DocRAG.Database/               # MongoDB repositories and context factory
DocRAG.Ingestion/              # Scraping, classification, chunking, embedding pipeline
  Crawling/                    #   Playwright web crawler + GitHub repo scraper
  Classification/              #   Ollama LLM page classifier
  Chunking/                    #   Category-aware semantic chunker
  Embedding/                   #   Ollama embedding provider
  Scanning/                    #   Project dependency scanner
  Ecosystems/                  #   NuGet, npm, pip registry clients
DocRAG.Mcp/                    # ASP.NET Core MCP server (SSE transport)
  Tools/                       #   MCP tool definitions (16 tools)
DocRAG.Cli/                    # Command-line interface
DocRAG.Tests/                  # Integration and unit tests
```

## How It Works

### Ingestion Pipeline

Pages flow through a streaming pipeline using `System.Threading.Channels`:

1. **Crawl** — Playwright fetches pages within configured URL patterns and depth limits. GitHub repository links are cloned and their markdown/docs scraped separately.
2. **Classify** — Each page is sent to the local LLM with the library hint. The classifier assigns one of: `Overview`, `HowTo`, `Sample`, `ApiReference`, `ChangeLog`, or `Unclassified`.
3. **Chunk** — Content is split at semantic boundaries (headings, code blocks) with section hierarchy preserved. API reference pages extract qualified names (e.g., `System.String.Format`).
4. **Embed** — Each chunk is embedded into a 768-dimensional vector using `nomic-embed-text`.
5. **Store** — Chunks are upserted into MongoDB. The in-memory vector index is refreshed automatically.

### Search

When your AI assistant calls `search_docs`:

1. The query is embedded using the same model
2. In-memory cosine similarity search finds the top candidates
3. Optional re-ranking via Ollama cross-encoder improves relevance
4. Results are returned with content, source URL, section path, and relevance score

### Dependency Scanning

`index_project_dependencies` parses your project files to discover packages:

| Ecosystem | Files Parsed | Registry |
|---|---|---|
| NuGet | `.csproj`, `.fsproj`, `packages.config` | api.nuget.org |
| npm | `package.json` | registry.npmjs.org |
| pip | `requirements.txt`, `setup.py`, `pyproject.toml` | pypi.org |

For each package, DocRAG resolves the documentation URL from the package registry metadata, checks whether it's already cached, and queues a scrape job for anything new.

## Supported Package Ecosystems

| Ecosystem | Project Files | Documentation Sources |
|---|---|---|
| **NuGet** (.NET) | `.csproj`, `.fsproj`, `packages.config` | learn.microsoft.com, GitHub README, project website |
| **npm** (JavaScript) | `package.json` | npmjs.com, GitHub README, project website |
| **pip** (Python) | `requirements.txt`, `setup.py`, `pyproject.toml` | readthedocs.io, sphinx docs, GitHub |

## Development

### Running Tests

```bash
dotnet test DocRAG.Tests/DocRAG.Tests.csproj
```

### VS Code

Launch configurations are included in `.vscode/launch.json`. Use the "Launch DocRAG.Mcp" configuration to start the MCP server with debugging.

### Development Settings

In `Development` mode (set via `ASPNETCORE_ENVIRONMENT=Development`):
- Only the default profile is bootstrapped at startup (faster startup)
- Re-ranking is disabled (saves Ollama resources during development)

## License

[MIT](LICENSE.txt) - Copyright 2012-Present [Jackalope Technologies, Inc.](https://github.com/JackalopeTechnologies) and Doug Gerard.
