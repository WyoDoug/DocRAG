// Program.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.CommandLine;
using System.Net;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database;
using DocRAG.Ingestion;
using DocRAG.Ingestion.Chunking;
using DocRAG.Ingestion.Classification;
using DocRAG.Ingestion.Crawling;
using DocRAG.Ingestion.Ecosystems.Common;
using DocRAG.Ingestion.Ecosystems.Npm;
using DocRAG.Ingestion.Ecosystems.NuGet;
using DocRAG.Ingestion.Ecosystems.Pip;
using DocRAG.Ingestion.Embedding;
using DocRAG.Ingestion.Scanning;
using DocRAG.Ingestion.Suspect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

#endregion


#region String constants

const string AppSettingsFile = "appsettings.json";
const string EnvironmentVariablePrefix = "DOCRAG_";
const string NuGetClientName = "NuGet";
const string NpmClientName = "npm";
const string PyPiClientName = "PyPI";
const string DocUrlProbeClientName = "DocUrlProbe";
const string RootCommandDescription = "DocRAG — Documentation Ingestion CLI";
const string IngestCommandName = "ingest";
const string IngestCommandDescription = "Ingest a documentation library";
const string RootUrlOptionName = "--root-url";
const string RootUrlOptionDescription = "Root URL to crawl";
const string LibraryIdOptionName = "--library-id";
const string UniqueLibraryIdDescription = "Unique library identifier";
const string VersionOptionName = "--version";
const string VersionStringDescription = "Version string";
const string HintOptionName = "--hint";
const string HintOptionDescription = "Library description hint";
const string AllowedOptionName = "--allowed";
const string AllowedOptionDescription = "Allowed URL patterns (regex)";
const string ExcludedOptionName = "--excluded";
const string ExcludedOptionDescription = "Excluded URL patterns (regex)";
const string MaxPagesOptionName = "--max-pages";
const string DelayOptionName = "--delay";
const string DelayOptionDescription = "Delay between fetches in ms";
const string ListCommandName = "list";
const string ListAllLibrariesDescription = "List all ingested libraries";
const string StatusCommandName = "status";
const string StatusCommandDescription = "Show ingestion status for a library";
const string LibraryIdDescription = "Library identifier";
const string DryrunCommandName = "dryrun";
const string DryrunCommandDescription = "Dry-run a scrape — fetch pages but store nothing";
const string ReclassifyCommandName = "reclassify";
const string ReclassifyCommandDescription = "Run the LLM classifier over existing pages still marked Unclassified, without re-scraping. Updates page records and chunk categories in MongoDB.";
const string ReclassifyLibraryIdDescription = "Library to reclassify (omit for all libraries)";
const string AllOptionName = "--all";
const string ReclassifyAllDescription = "Reclassify ALL pages, even ones already classified";
const string InspectCommandName = "inspect";
const string InspectCommandDescription = "Load a single page and report its link/sidebar structure";
const string UrlOptionName = "--url";
const string UrlOptionDescription = "URL to inspect";
const string CollapsedPropertyName = "collapsed";
const string SidebarsPropertyName = "sidebars";
const string IdPropertyName = "id";
const string ClassNamePropertyName = "className";
const string SamplesPropertyName = "samples";
const string LinksByHostPropertyName = "linksByHost";
const string ProfileCommandName = "profile";
const string ProfileCommandDescription = "Show or switch MongoDB connection profiles";
const string ListAvailableProfilesDescription = "List available profiles";
const string MongoDbProfileEnvVar = "DOCRAG_MONGODB_PROFILE";
const string ActiveMarker = " ←";
const string ScanCommandName = "scan";
const string ScanCommandDescription = "Scan project dependencies and index documentation";
const string PathOptionName = "--path";
const string PathOptionDescription = "Path to project root, .sln, .csproj, package.json, or requirements.txt";
const string ProfileOptionName = "--profile";
const string ProfileOptionDescription = "Database profile name";
const string QueuedStatus = "queued";
const string FailedStatus = "failed";

#endregion
// Build configuration
var configuration = new ConfigurationBuilder()
                    .AddJsonFile(AppSettingsFile, optional: true)
                    .AddEnvironmentVariables(EnvironmentVariablePrefix)
                    .Build();

// Build DI container
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());
services.AddDocRagDatabase(configuration);
services.Configure<OllamaSettings>(configuration.GetSection(OllamaSettings.SectionName));
services.AddSingleton<OllamaBootstrapper>();
services.AddSingleton<GitHubRepoScraper>();
services.AddSingleton<PageCrawler>();
services.AddSingleton<LlmClassifier>();
services.AddSingleton<DocRAG.Ingestion.Symbols.SymbolExtractor>();
services.AddSingleton<DocRAG.Ingestion.Recon.LibraryProfileService>();
services.AddSingleton<DocRAG.Ingestion.Recon.CliReconFallback>();
services.AddSingleton<DocRAG.Ingestion.Recon.RescrubService>();
services.AddSingleton<CategoryAwareChunker>();
services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();
services.AddSingleton<IVectorSearchProvider, InMemoryBruteForceVectorSearch>();
services.AddSingleton<SuspectDetector>();
services.AddSingleton<IngestionOrchestrator>();

// Scrape job queue (required by DependencyIndexer)
services.AddSingleton<ScrapeJobRunner>();
services.AddSingleton<IScrapeJobQueue>(sp =>
                                           sp.GetRequiredService<ScrapeJobRunner>()
                                      );

// HTTP clients for package registry APIs
services.AddHttpClient(NuGetClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                                                      {
                                                          AutomaticDecompression = DecompressionMethods.All
                                                      }
                                           );
services.AddHttpClient(NpmClientName);
services.AddHttpClient(PyPiClientName);
services.AddHttpClient(DocUrlProbeClientName);

// Shared utilities
services.AddSingleton<CommonDocUrlPatterns>();
services.AddSingleton<PackageFilter>();

// NuGet ecosystem
services.AddSingleton<IProjectFileParser, NuGetProjectFileParser>();
services.AddSingleton<IPackageRegistryClient, NuGetRegistryClient>();
services.AddSingleton<IDocUrlResolver, NuGetDocUrlResolver>();

// npm ecosystem
services.AddSingleton<IProjectFileParser, NpmProjectFileParser>();
services.AddSingleton<IPackageRegistryClient, NpmRegistryClient>();
services.AddSingleton<IDocUrlResolver, NpmDocUrlResolver>();

// pip ecosystem
services.AddSingleton<IProjectFileParser, PipProjectFileParser>();
services.AddSingleton<IPackageRegistryClient, PyPiRegistryClient>();
services.AddSingleton<IDocUrlResolver, PipDocUrlResolver>();

// Dependency indexing orchestrator
services.AddSingleton<DependencyIndexer>();

var provider = services.BuildServiceProvider();

// Root command
var rootCommand = new RootCommand(RootCommandDescription);

// ingest command
var ingestCommand = new Command(IngestCommandName, IngestCommandDescription);
var rootUrlOption = new Option<string>(RootUrlOptionName, RootUrlOptionDescription) { IsRequired = true };
var libraryIdOption = new Option<string>(LibraryIdOptionName, UniqueLibraryIdDescription) { IsRequired = true };
var versionOption = new Option<string>(VersionOptionName, VersionStringDescription) { IsRequired = true };
var hintOption = new Option<string>(HintOptionName, HintOptionDescription) { IsRequired = true };
var allowedOption = new Option<string[]>(AllowedOptionName, AllowedOptionDescription)
                        { IsRequired = true, AllowMultipleArgumentsPerToken = true };
var excludedOption = new Option<string[]>(ExcludedOptionName, ExcludedOptionDescription)
                         { AllowMultipleArgumentsPerToken = true };
var maxPagesOption = new Option<int>(MaxPagesOptionName, () => 0, "Max pages to crawl (0 = unlimited)");
var delayOption = new Option<int>(DelayOptionName, () => ScrapeJob.DefaultFetchDelayMs, DelayOptionDescription);

ingestCommand.AddOption(rootUrlOption);
ingestCommand.AddOption(libraryIdOption);
ingestCommand.AddOption(versionOption);
ingestCommand.AddOption(hintOption);
ingestCommand.AddOption(allowedOption);
ingestCommand.AddOption(excludedOption);
ingestCommand.AddOption(maxPagesOption);
ingestCommand.AddOption(delayOption);

ingestCommand.SetHandler(async (rootUrl,
                                libraryId,
                                version,
                                hint,
                                allowed,
                                excluded,
                                maxPages,
                                delay) =>
                         {
                             // Bootstrap Ollama: install if missing, start if stopped, pull required models
                             var bootstrapper = provider.GetRequiredService<OllamaBootstrapper>();
                             await bootstrapper.BootstrapAsync();

                             var job = new ScrapeJob
                                           {
                                               RootUrl = rootUrl,
                                               LibraryId = libraryId,
                                               Version = version,
                                               LibraryHint = hint,
                                               AllowedUrlPatterns = allowed,
                                               ExcludedUrlPatterns = excluded ?? [],
                                               MaxPages = maxPages,
                                               FetchDelayMs = delay
                                           };

                             var orchestrator = provider.GetRequiredService<IngestionOrchestrator>();
                             await orchestrator.IngestAsync(job,
                                                            onProgress: progress =>
                                                                        {
                                                                            Console
                                                                                .Write($"\rQueued: {progress.PagesQueued} | Crawled: {progress.PagesFetched} | " +
                                                                                         $"Classified: {progress.PagesClassified} | Chunks: {progress.ChunksGenerated} | " +
                                                                                         $"Searchable: {progress.ChunksCompleted} chunks ({progress.PagesCompleted} pages)"
                                                                                    );
                                                                        }
                                                           );
                             Console.WriteLine();
                         },
                         rootUrlOption,
                         libraryIdOption,
                         versionOption,
                         hintOption,
                         allowedOption,
                         excludedOption,
                         maxPagesOption,
                         delayOption
                        );

// list command
var listCommand = new Command(ListCommandName, ListAllLibrariesDescription);
listCommand.SetHandler(async () =>
                       {
                           var repo = provider.GetRequiredService<ILibraryRepository>();
                           var libraries = await repo.GetAllLibrariesAsync();

                           if (libraries.Count == 0)
                               Console.WriteLine("No libraries ingested yet.");
                           else
                           {
                               foreach(var lib in libraries)
                                   Console
                                       .WriteLine($"  {lib.Id} — {lib.Name} (current: {lib.CurrentVersion}, versions: {string.Join(", ", lib.AllVersions)})"
                                                 );
                           }
                       }
                      );

// status command
var statusCommand = new Command(StatusCommandName, StatusCommandDescription);
var statusLibOption = new Option<string>(LibraryIdOptionName, LibraryIdDescription) { IsRequired = true };
statusCommand.AddOption(statusLibOption);
statusCommand.SetHandler(async libraryId =>
                         {
                             var libRepo = provider.GetRequiredService<ILibraryRepository>();
                             var pageRepo = provider.GetRequiredService<IPageRepository>();
                             var chunkRepo = provider.GetRequiredService<IChunkRepository>();

                             var lib = await libRepo.GetLibraryAsync(libraryId);
                             if (lib == null)
                                 Console.WriteLine($"Library '{libraryId}' not found.");
                             else
                             {
                                 Console.WriteLine($"Library: {lib.Name} ({lib.Id})");
                                 Console.WriteLine($"Current Version: {lib.CurrentVersion}");
                                 Console.WriteLine($"All Versions: {string.Join(", ", lib.AllVersions)}");

                                 foreach(var ver in lib.AllVersions)
                                 {
                                     int pages = await pageRepo.GetPageCountAsync(libraryId, ver);
                                     int chunks = await chunkRepo.GetChunkCountAsync(libraryId, ver);
                                     Console.WriteLine($"  v{ver}: {pages} pages, {chunks} chunks");
                                 }
                             }
                         },
                         statusLibOption
                        );

// dryrun command
var dryrunCommand = new Command(DryrunCommandName, DryrunCommandDescription);
dryrunCommand.AddOption(rootUrlOption);
dryrunCommand.AddOption(allowedOption);
dryrunCommand.AddOption(excludedOption);
dryrunCommand.AddOption(maxPagesOption);
dryrunCommand.AddOption(delayOption);

dryrunCommand.SetHandler(async (rootUrl,
                                allowed,
                                excluded,
                                maxPages,
                                delay) =>
                         {
                             var job = new ScrapeJob
                                           {
                                               RootUrl = rootUrl,
                                               LibraryId = "dryrun",
                                               Version = "dryrun",
                                               LibraryHint = "Dry run",
                                               AllowedUrlPatterns = allowed,
                                               ExcludedUrlPatterns = excluded ?? [],
                                               MaxPages = maxPages,
                                               FetchDelayMs = delay
                                           };

                             var crawler = provider.GetRequiredService<PageCrawler>();
                             var report = await crawler.DryRunAsync(job);

                             Console.WriteLine();
                             Console.WriteLine($"=== Dry Run Report ({report.ElapsedTime.TotalSeconds:F1}s) ===");
                             Console.WriteLine($"Total pages fetched: {report.TotalPages}");
                             Console.WriteLine($"  In-scope:    {report.InScopePages}");
                             Console.WriteLine($"  Out-of-scope: {report.OutOfScopePages}");
                             Console.WriteLine($"Skipped (filtered): {report.FilteredSkips}");
                             Console.WriteLine($"Skipped (depth limit): {report.DepthLimitedSkips}");
                             Console.WriteLine($"Fetch errors: {report.FetchErrors}");
                             Console.WriteLine($"Pages still in queue at end: {report.PagesRemainingInQueue}");
                             if (report.HitMaxPagesLimit)
                                 Console
                                     .WriteLine($"** HIT MaxPages limit ({maxPages}) — actual crawl would have {report.TotalPages + report.PagesRemainingInQueue}+ pages **"
                                               );

                             Console.WriteLine();
                             Console.WriteLine("Pages by host:");
                             foreach((var host, var count) in report.PagesByHost.OrderByDescending(kv => kv.Value))
                                 Console.WriteLine($"  {host}: {count}");

                             Console.WriteLine();
                             Console.WriteLine("Out-of-scope depth distribution:");
                             foreach((var depth, var count) in report.DepthDistribution.OrderBy(kv => kv.Key))
                                 Console.WriteLine($"  depth {depth}: {count}");

                             if (report.GitHubReposToClone.Count > 0)
                             {
                                 Console.WriteLine();
                                 Console
                                     .WriteLine($"GitHub repos that would be cloned ({report.GitHubReposToClone.Count}):"
                                               );
                                 foreach(var repo in report.GitHubReposToClone)
                                     Console.WriteLine($"  {repo}");
                             }

                             if (report.Errors.Count > 0)
                             {
                                 Console.WriteLine();
                                 Console.WriteLine($"Fetch errors ({report.Errors.Count}):");
                                 var grouped = report.Errors.GroupBy(e => e.ErrorKind)
                                                     .OrderByDescending(g => g.Count());
                                 foreach(var group in grouped)
                                 {
                                     Console.WriteLine($"  [{group.Count()}] {group.Key}");
                                     foreach(var err in group.Take(count: 5))
                                         Console.WriteLine($"    {err.Url} — {err.Message}");
                                     if (group.Count() > 5)
                                         Console.WriteLine($"    ... and {group.Count() - 5} more");
                                 }
                             }

                             if (report.SamplePendingUrls.Count > 0)
                             {
                                 Console.WriteLine();
                                 Console
                                     .WriteLine($"Sample URLs still in queue (first {report.SamplePendingUrls.Count}):"
                                               );
                                 foreach(var pending in report.SamplePendingUrls)
                                     Console.WriteLine($"  {pending}");
                             }
                         },
                         rootUrlOption,
                         allowedOption,
                         excludedOption,
                         maxPagesOption,
                         delayOption
                        );

// reclassify command — re-run LLM classifier over already-ingested pages
var reclassifyCommand = new Command(ReclassifyCommandName,
                                    ReclassifyCommandDescription
                                   );
var reclassifyLibOption = new Option<string?>(LibraryIdOptionName, ReclassifyLibraryIdDescription);
var reclassifyAllOption = new Option<bool>(AllOptionName, () => false, ReclassifyAllDescription);
reclassifyCommand.AddOption(reclassifyLibOption);
reclassifyCommand.AddOption(reclassifyAllOption);

reclassifyCommand.SetHandler(async (libraryId, allPages) =>
                             {
                                 var bootstrapper = provider.GetRequiredService<OllamaBootstrapper>();
                                 await bootstrapper.BootstrapAsync();

                                 var libRepo = provider.GetRequiredService<ILibraryRepository>();
                                 var pageRepo = provider.GetRequiredService<IPageRepository>();
                                 var chunkRepo = provider.GetRequiredService<IChunkRepository>();
                                 var llm = provider.GetRequiredService<LlmClassifier>();

                                 var libraries = string.IsNullOrEmpty(libraryId)
                                                     ? await libRepo.GetAllLibrariesAsync()
                                                     : new List<LibraryRecord>
                                                           {
                                                               await libRepo.GetLibraryAsync(libraryId) ??
                                                               throw new Exception($"Library '{libraryId}' not found")
                                                           };

                                 var totalProcessed = 0;
                                 var totalReclassified = 0;

                                 foreach(var lib in libraries)
                                 {
                                     Console.WriteLine($"\nReclassifying {lib.Id} v{lib.CurrentVersion}...");
                                     var pages = await pageRepo.GetPagesAsync(lib.Id, lib.CurrentVersion);
                                     var targetPages = allPages
                                                           ? pages.ToList()
                                                           : pages.Where(p => p.Category == DocCategory.Unclassified)
                                                                  .ToList();

                                     Console
                                         .WriteLine($"  {targetPages.Count} pages to process (of {pages.Count} total)");

                                     var processed = 0;
                                     foreach(var page in targetPages)
                                     {
                                         (var newCategory, var confidence) = await llm.ClassifyAsync(page, lib.Hint);
                                         processed++;

                                         if (newCategory != DocCategory.Unclassified &&
                                             confidence > 0 &&
                                             newCategory != page.Category)
                                         {
                                             var classified = page with { Category = newCategory };
                                             await pageRepo.UpsertPageAsync(classified);

                                             await chunkRepo.UpdateCategoryByPageUrlAsync(lib.Id,
                                                      lib.CurrentVersion,
                                                      page.Url,
                                                      newCategory
                                                 );

                                             totalReclassified++;
                                         }

                                         if (processed % 10 == 0)
                                             Console
                                                 .WriteLine($"  {processed}/{targetPages.Count} processed, {totalReclassified} reclassified so far"
                                                           );
                                     }

                                     totalProcessed += processed;
                                 }

                                 Console
                                     .WriteLine($"\nDone. Processed {totalProcessed} pages, reclassified {totalReclassified}."
                                               );
                                 Console
                                     .WriteLine("Pages and chunks updated in MongoDB. Restart MCP server (or call reload_profile) to refresh in-memory index."
                                               );
                             },
                             reclassifyLibOption,
                             reclassifyAllOption
                            );

// inspect command — load a single page, dump TOC/sidebar info
var inspectCommand = new Command(InspectCommandName, InspectCommandDescription);
var inspectUrlOption = new Option<string>(UrlOptionName, UrlOptionDescription) { IsRequired = true };
inspectCommand.AddOption(inspectUrlOption);
inspectCommand.SetHandler(async url =>
                          {
                              using var playwright = await Playwright.CreateAsync();
                              await using var browser =
                                  await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }
                                                                       );
                              var page = await browser.NewPageAsync();

                              Console.WriteLine($"Loading {url}...");
                              await page.GotoAsync(url,
                                                   new PageGotoOptions
                                                       {
                                                           WaitUntil = WaitUntilState.NetworkIdle,
                                                           Timeout = 60000
                                                       }
                                                  );

                              // Extra wait for deferred JS (like TOC loaders)
                              await Task.Delay(millisecondsDelay: 3000);

                              var infoJson = await page.EvaluateAsync<string>(expression: """
                                                                                  (() => {
                                                                                      const result = {};
                                                                                      result.totalLinks = document.querySelectorAll('a[href]').length;
                                                                                      result.title = document.title;

                                                                                      const candidates = [
                                                                                          'nav', 'aside',
                                                                                          '[class*="sidebar" i]', '[class*="toc" i]', '[class*="tree" i]',
                                                                                          '[id*="sidebar" i]', '[id*="toc" i]', '[id*="tree" i]', '[id*="nav" i]',
                                                                                          '.left-nav', '.left-menu', '.doc-nav', '.help-nav'
                                                                                      ];
                                                                                      const sidebars = [];
                                                                                      const seen = new Set();
                                                                                      for (const sel of candidates) {
                                                                                          try {
                                                                                              const els = document.querySelectorAll(sel);
                                                                                              for (const el of els) {
                                                                                                  if (seen.has(el)) continue;
                                                                                                  seen.add(el);
                                                                                                  const linkCount = el.querySelectorAll('a[href]').length;
                                                                                                  if (linkCount > 5) {
                                                                                                      sidebars.push({
                                                                                                          sel: sel,
                                                                                                          tag: el.tagName,
                                                                                                          id: el.id || '',
                                                                                                          className: ((el.className || '') + '').substring(0, 120),
                                                                                                          linkCount: linkCount,
                                                                                                          samples: Array.from(el.querySelectorAll('a[href]')).slice(0, 3).map(function(a) { return a.href; })
                                                                                                      });
                                                                                                  }
                                                                                              }
                                                                                          } catch (e) {}
                                                                                      }
                                                                                      result.sidebars = sidebars;

                                                                                      result.collapsed = {
                                                                                          ariaCollapsed: document.querySelectorAll('[aria-expanded="false"]').length,
                                                                                          collapsedClass: document.querySelectorAll('.collapsed').length,
                                                                                          hiddenClass: document.querySelectorAll('.hidden, .hide').length,
                                                                                          treeNodes: document.querySelectorAll('[class*="tree-node" i], [class*="treenode" i]').length
                                                                                      };

                                                                                      const hosts = {};
                                                                                      document.querySelectorAll('a[href]').forEach(function(a) {
                                                                                          try {
                                                                                              const u = new URL(a.href);
                                                                                              hosts[u.host] = (hosts[u.host] || 0) + 1;
                                                                                          } catch (e) {}
                                                                                      });
                                                                                      result.linksByHost = hosts;

                                                                                      return JSON.stringify(result);
                                                                                  })()
                                                                                  """
                                                                             );

                              Console.WriteLine();
                              using var doc = JsonDocument.Parse(infoJson);
                              var root = doc.RootElement;

                              Console.WriteLine($"Title: {root.GetProperty("title").GetString()}");
                              Console.WriteLine($"Total links: {root.GetProperty("totalLinks").GetInt32()}");
                              Console.WriteLine();

                              Console.WriteLine("Collapsible markers:");
                              var collapsed = root.GetProperty(CollapsedPropertyName);
                              foreach(var prop in collapsed.EnumerateObject())
                                  Console.WriteLine($"  {prop.Name}: {prop.Value.GetInt32()}");
                              Console.WriteLine();

                              Console.WriteLine("Sidebar candidates with >5 links:");
                              foreach(var sb in root.GetProperty(SidebarsPropertyName).EnumerateArray())
                              {
                                  Console
                                      .WriteLine($"  [{sb.GetProperty("linkCount").GetInt32()} links] {sb.GetProperty("tag").GetString()}" +
                                                 (sb.GetProperty(IdPropertyName).GetString() is var id &&
                                                  !string.IsNullOrEmpty(id)
                                                      ? $"#{id}"
                                                      : string.Empty) +
                                                 (sb.GetProperty(ClassNamePropertyName).GetString() is var cls &&
                                                  !string.IsNullOrEmpty(cls)
                                                      ? $" .{cls.Replace(" ", ".")}"
                                                      : string.Empty)
                                                );
                                  Console.WriteLine($"    selector hint: {sb.GetProperty("sel").GetString()}");
                                  foreach(var sample in sb.GetProperty(SamplesPropertyName).EnumerateArray())
                                      Console.WriteLine($"    sample: {sample.GetString()}");
                              }

                              Console.WriteLine();

                              Console.WriteLine("Links by host:");
                              foreach(var prop in root.GetProperty(LinksByHostPropertyName).EnumerateObject())
                                  Console.WriteLine($"  {prop.Name}: {prop.Value.GetInt32()}");

                              await page.CloseAsync();
                          },
                          inspectUrlOption
                         );

// profile command
var profileCommand = new Command(ProfileCommandName, ProfileCommandDescription);

var profileListCommand = new Command(ListCommandName, ListAvailableProfilesDescription);
profileListCommand.SetHandler(() =>
                              {
                                  var settings = provider.GetRequiredService<IOptions<DocRagDbSettings>>().Value;
                                  var activeOverride = Environment.GetEnvironmentVariable(MongoDbProfileEnvVar);
                                  (var activeConn, var activeDb) = settings.Resolve();

                                  Console.WriteLine($"Active: {activeOverride ?? settings.ActiveProfile ?? "(direct)"}"
                                                   );
                                  Console.WriteLine($"Connected to: {activeConn} / {activeDb}");
                                  Console.WriteLine();

                                  if (settings.Profiles.Count == 0)
                                      Console
                                          .WriteLine("No profiles defined. Using direct ConnectionString/DatabaseName."
                                                    );
                                  else
                                  {
                                      foreach((var name, var profile) in settings.Profiles)
                                      {
                                          var isActive = name.Equals(activeOverride ?? settings.ActiveProfile,
                                                                     StringComparison.OrdinalIgnoreCase
                                                                    );
                                          var marker = isActive ? ActiveMarker : string.Empty;
                                          Console
                                              .WriteLine($"  {name}: {profile.ConnectionString} / {profile.DatabaseName}{marker}"
                                                        );
                                          if (!string.IsNullOrEmpty(profile.Description))
                                              Console.WriteLine($"    {profile.Description}");
                                      }
                                  }

                                  Console.WriteLine();
                                  Console.WriteLine("Switch profiles:");
                                  Console.WriteLine("  Set DOCRAG_MONGODB_PROFILE=company  (environment variable)");
                                  Console.WriteLine("  Or edit ActiveProfile in appsettings.json");
                              }
                             );

profileCommand.AddCommand(profileListCommand);

// scan command — scan project dependencies and index documentation
var scanCommand = new Command(ScanCommandName, ScanCommandDescription);
var scanPathOption =
    new Option<string>(PathOptionName, PathOptionDescription)
        { IsRequired = true };
var scanProfileOption = new Option<string?>(ProfileOptionName, ProfileOptionDescription);
scanCommand.AddOption(scanPathOption);
scanCommand.AddOption(scanProfileOption);

scanCommand.SetHandler(async (path, profile) =>
                       {
                           var indexer = provider.GetRequiredService<DependencyIndexer>();
                           var report = await indexer.IndexProjectAsync(path, profile, CancellationToken.None);

                           Console.WriteLine();
                           Console.WriteLine("=== Dependency Scan Report ===");
                           Console.WriteLine($"Project path:             {report.ProjectPath}");
                           Console.WriteLine($"Total dependencies found:  {report.TotalDependencies}");
                           Console.WriteLine($"Filtered out:              {report.FilteredOut}");
                           Console.WriteLine($"Already cached:            {report.AlreadyCached}");
                           Console.WriteLine($"Cached (different version): {report.CachedDifferentVersion}");
                           Console.WriteLine($"Newly queued:              {report.NewlyQueued}");
                           Console.WriteLine($"Resolution failed:         {report.ResolutionFailed}");

                           var queuedPackages = report.Packages
                                                      .Where(p => p.Status == QueuedStatus)
                                                      .ToList();

                           if (queuedPackages.Count > 0)
                           {
                               Console.WriteLine();
                               Console.WriteLine($"Queued for scraping ({queuedPackages.Count}):");
                               foreach(var pkg in queuedPackages)
                                   Console
                                       .WriteLine($"  {pkg.EcosystemId}/{pkg.PackageId} {pkg.Version} -> {pkg.DocUrl}");
                           }

                           var failedPackages = report.Packages
                                                      .Where(p => p.Status == FailedStatus)
                                                      .ToList();

                           if (failedPackages.Count > 0)
                           {
                               Console.WriteLine();
                               Console.WriteLine($"Failed ({failedPackages.Count}):");
                               foreach(var pkg in failedPackages)
                                   Console
                                       .WriteLine($"  {pkg.EcosystemId}/{pkg.PackageId} {pkg.Version} — {pkg.ErrorMessage}"
                                                 );
                           }
                       },
                       scanPathOption,
                       scanProfileOption
                      );

rootCommand.AddCommand(ingestCommand);
rootCommand.AddCommand(dryrunCommand);
rootCommand.AddCommand(inspectCommand);
rootCommand.AddCommand(reclassifyCommand);
rootCommand.AddCommand(scanCommand);
rootCommand.AddCommand(listCommand);
rootCommand.AddCommand(statusCommand);
rootCommand.AddCommand(profileCommand);

return await rootCommand.InvokeAsync(args);
