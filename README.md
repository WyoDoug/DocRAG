# SaddleRAG

**Documentation Retrieval-Augmented Generation for AI coding assistants.**

SaddleRAG scrapes documentation websites, classifies and chunks the content with a local LLM, generates vector embeddings, and stores everything in MongoDB. It exposes the indexed documentation through MCP (Model Context Protocol) tools so that AI assistants like Claude Code, GitHub Copilot, and others can search your documentation library in real time.

## Why SaddleRAG?

AI coding assistants are limited by their training cutoff and context window. When you're working with a niche library, a new release, or internal documentation, the assistant doesn't know about it. SaddleRAG bridges that gap:

- **Scrape any documentation site** into a searchable vector database
- **Auto-index project dependencies** from NuGet, npm, and pip
- **Serve documentation to your AI assistant** via MCP tools during coding sessions
- **Track multiple versions** of the same library and diff changes between them
- **Share a company-wide documentation database** across your team

## Architecture

```
Documentation Sites          SaddleRAG Pipeline                    AI Assistants
==================          ===============                    ==============

docs.example.com  --+
                    |      +-------------+
github.com/repo   --+-->   |  Playwright  |  (headless browser)
                    |      |   Crawler    |
learn.microsoft   --+      +------+------+
                                  |
                           +------v------+
                           |   Ollama    |  (local LLM)
                           |  Classifier |  qwen3:1.7b
                           +------+------+
                                  |
                           +------v------+
                           |  Category-  |
                           |   Aware     |
                           |  Chunker    |
                           +------+------+
                                  |
                           +------v------+
                           |   Ollama    |  nomic-embed-text
                           |  Embedder   |  (768 dimensions)
                           +------+------+
                                  |
                           +------v------+     +--------------+
                           |   MongoDB   |<--->|  MCP Server  |--> Claude Code
                           |  (storage)  |     |   (HTTP)     |--> Copilot
                           +-------------+     +--------------+--> Any MCP client
```

## Quick Start (Windows Installer)

The fastest way to get SaddleRAG running is the MSI installer from [GitHub Releases](https://github.com/JackalopeTechnologies/saddlerag/releases). It installs SaddleRAG as a Windows service, configures connections to MongoDB and Ollama, and starts automatically.

SaddleRAG requires two free, open-source tools as prerequisites. Both are available as community editions at no cost.

### Step 1: Install MongoDB Community Edition (free)

MongoDB stores all scraped documentation, chunks, and vector embeddings.

1. Download the **Community Edition** from [mongodb.com/try/download/community](https://www.mongodb.com/try/download/community)
2. Run the installer, choose **Complete** setup type
3. Keep the default settings: **port 27017**, **Run as a Service** checked
4. After install, verify it's running: open a terminal and run `mongosh` -- you should see a connection prompt

> **Using Docker or a remote server?** No problem. The SaddleRAG installer lets you enter any MongoDB connection string (e.g. `mongodb://your-server:27017`). You can also run MongoDB in Docker: `docker run -d -p 27017:27017 --name saddlerag-mongo mongo:latest`

### Step 2: Install Ollama (free)

Ollama runs AI models locally for document classification and embedding generation. No API keys or cloud accounts needed.

1. Download from [ollama.com](https://ollama.com)
2. Run the installer -- Ollama runs as a background service on **port 11434**
3. After install, verify it's running: open a terminal and run `ollama list`

SaddleRAG automatically pulls the required models on first use:
- `nomic-embed-text` -- generates vector embeddings (768 dimensions)
- `qwen3:1.7b` -- classifies documentation pages and optional re-ranking

> **Running Ollama elsewhere?** The SaddleRAG installer lets you point to any Ollama endpoint (e.g. `http://your-gpu-server:11434`).

### Step 3: Install SaddleRAG

1. Download `SaddleRAG.Mcp.msi` from the [latest release](https://github.com/JackalopeTechnologies/saddlerag/releases/latest)
2. Run the installer
3. **MongoDB Configuration** -- the installer defaults to `mongodb://localhost:27017` with database `SaddleRAG`. Use the **Test Connection** button to verify MongoDB is reachable. If your MongoDB is on a different host, enter the connection string. **Reset to Local Defaults** reverts to the standard local settings.
4. **Ollama Configuration** -- defaults to `http://localhost:11434`. Use **Test Connection** to verify. Change only if Ollama is running on another machine.
5. Click **Install** -- files are copied to `Program Files\SaddleRAG\SaddleRAG.Mcp`, your connection settings are written to `appsettings.json`, and the **SaddleRAGMcp** Windows service starts automatically.

> **Don't have the prerequisites yet?** The installer includes **Download** buttons on each configuration page that open your browser to the MongoDB and Ollama download pages. Install them, then click **Test Connection** to verify before proceeding.

### Step 4: Connect Your AI Assistant

Add this to your MCP client configuration. For **Claude Code**, create a `.mcp.json` file in your project root or home directory:

```json
{
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 60
    }
  }
}
```

### Step 5: Verify

Open your AI assistant and ask it to list libraries:

> "Use the list_libraries tool to show what documentation is indexed."

If SaddleRAG is running, you'll get an empty list (nothing indexed yet). Then try:

> "Scrape the documentation at https://docs.example.com for me."

The assistant will use the `scrape_docs` tool to index the site.

### Verify the Service

- **Health check**: visit `http://localhost:6100/health` in a browser
- **Service status**: run `Get-Service SaddleRAGMcp` in PowerShell
- **Logs**: check `%ProgramData%\SaddleRAG\logs\` or use the `get_server_logs` MCP tool

## Quick Start (Developer / Build from Source)

If you want to build and run from source instead of the MSI:

### Prerequisites

| Dependency | Version | Purpose |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0+ | Build and run |
| [MongoDB](https://www.mongodb.com/try/download/community) | 6.0+ | Document storage (port 27017) |
| [Ollama](https://ollama.com) | Latest | Local LLM for embeddings (port 11434) |

### Build and Run

```bash
git clone https://github.com/JackalopeTechnologies/saddlerag.git
cd SaddleRAG
dotnet build SaddleRAG.slnx
dotnet run --project SaddleRAG.Mcp
```

The server starts on `http://localhost:6100` by default. Configuration is in `SaddleRAG.Mcp/appsettings.Development.json`.

### Connect Your AI Assistant

Add to `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "saddlerag": {
      "type": "http",
      "url": "http://localhost:6100/mcp",
      "timeout": 60
    }
  }
}
```

## MCP Tools Reference

SaddleRAG exposes 16 tools through the MCP protocol. Your AI assistant discovers these automatically once connected.

### Search

| Tool | Description |
|---|---|
| `search_docs` | Natural language search across all libraries or filtered by library, version, and category (Overview, HowTo, Sample, ApiReference, ChangeLog) |
| `get_class_reference` | Look up API reference for a class or type by name. Searches across all libraries if none specified. Tries exact match, then fuzzy. |
| `get_library_overview` | Get Overview-category chunks for a library - concepts, architecture, getting started guides |

### Library Management

| Tool | Description |
|---|---|
| `list_libraries` | List all indexed libraries with current version and all ingested versions |
| `list_classes` | List all documented classes/types for a library, with optional name filter |

### Ingestion

| Tool | Description |
|---|---|
| `scrape_docs` | Scrape a documentation URL with auto-derived crawl settings. Cache-aware - skips already-indexed libraries unless `force=true` |
| `scrape_library` | Queue a scrape job with full control over URL patterns, depth, and delay. Returns a job ID for polling. |
| `dryrun_scrape` | Test a scrape configuration without writing to the database. Reports page counts, depth distribution, and GitHub repos that would be cloned. |
| `continue_scrape` | Resume an interrupted or MaxPages-limited scrape from where it left off |
| `get_scrape_status` | Poll a scrape job's progress by job ID |
| `list_scrape_jobs` | List recent scrape jobs with status |
| `index_project_dependencies` | Scan a project's NuGet/npm/pip dependencies and auto-index their documentation |

### Version Management

| Tool | Description |
|---|---|
| `get_version_changes` | Diff two versions of a library - added, removed, and changed pages with summaries |

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
dotnet build SaddleRAG.Cli/SaddleRAG.Cli.csproj
```

### Commands

**Ingest a documentation library:**
```bash
saddlerag ingest \
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
saddlerag dryrun \
  --root-url https://docs.example.com/ \
  --allowed "docs.example.com" \
  --max-pages 200
```

**Inspect a page's link/sidebar structure (useful for tuning URL patterns):**
```bash
saddlerag inspect --url https://docs.example.com/getting-started
```

**List indexed libraries:**
```bash
saddlerag list
```

**Show ingestion status:**
```bash
saddlerag status --library-id example-lib
```

**Re-classify pages with the LLM (fix unclassified pages):**
```bash
saddlerag reclassify --library-id example-lib
saddlerag reclassify --all  # Reclassify everything, even already-classified pages
```

**Scan project dependencies and auto-index:**
```bash
saddlerag scan --path ./MyProject.sln
saddlerag scan --path ./package.json --profile company
```

**Manage database profiles:**
```bash
saddlerag profile list
```

## Configuration

### MongoDB Profiles

SaddleRAG supports multiple MongoDB databases via named profiles. Configure them in `appsettings.json`:

```json
{
  "MongoDB": {
    "ActiveProfile": "local",
    "Profiles": {
      "local": {
        "ConnectionString": "mongodb://localhost:27017",
        "DatabaseName": "SaddleRAG",
        "Description": "Local development database"
      },
      "company": {
        "ConnectionString": "mongodb://saddlerag.internal.company.com:27017",
        "DatabaseName": "SaddleRAG",
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

All settings can be overridden via environment variables prefixed with `SADDLERAG_`:

```bash
SADDLERAG_MONGODB_PROFILE=company          # Override active profile
ASPNETCORE_ENVIRONMENT=Development      # Enable dev settings (disables re-ranking)
```

## Releasing

To create a new release with an MSI installer:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The CI pipeline builds the solution, runs tests, packages the MSI, and attaches it to a GitHub Release automatically.

## Project Structure

```
SaddleRAG.slnx                    # Solution file
SaddleRAG.Core/                   # Domain models, interfaces, enums
SaddleRAG.Database/               # MongoDB repositories and context factory
SaddleRAG.Ingestion/              # Scraping, classification, chunking, embedding pipeline
  Crawling/                    #   Playwright web crawler + GitHub repo scraper
  Classification/              #   Ollama LLM page classifier
  Chunking/                    #   Category-aware semantic chunker
  Embedding/                   #   Ollama embedding provider
  Scanning/                    #   Project dependency scanner
  Ecosystems/                  #   NuGet, npm, pip registry clients
SaddleRAG.Mcp/                    # ASP.NET Core MCP server (HTTP transport)
  Tools/                       #   MCP tool definitions (16 tools)
SaddleRAG.Cli/                    # Command-line interface
SaddleRAG.Installer/              # WiX MSI installer definition
SaddleRAG.Tests/                  # Integration and unit tests
```
