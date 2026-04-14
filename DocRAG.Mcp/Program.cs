// // Program.cs

// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.



#region Usings



using System.Diagnostics;

using System.Net;

using DocRAG.Core.Interfaces;

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

using DocRAG.Mcp;

using DocRAG.Mcp.Tools;

using ModelContextProtocol.Protocol;

using Serilog;
using Serilog.Core;

using Serilog.Events;



#endregion



const string AppName = "DocRAG";
const string LogSubdirectory = "logs";
const string MicrosoftAspNetCoreNamespace = "Microsoft.AspNetCore";
const string LogFileNamePattern = "docrag-.log";
const string HttpClientNuGet = "NuGet";
const string HttpClientNpm = "npm";
const string HttpClientPyPi = "PyPI";
const string HttpClientDocUrlProbe = "DocUrlProbe";
const string KestrelHttpsEndpointKey = "Kestrel:Endpoints:Https:Url";
const string HealthEndpointPath = "/health";
const string HealthyStatus = "Healthy";

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),

                                AppName,

                                LogSubdirectory

                               );

Directory.CreateDirectory(logDirectory);



var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);

Log.Logger = new LoggerConfiguration()

             .MinimumLevel.ControlledBy(levelSwitch)

             .MinimumLevel.Override(MicrosoftAspNetCoreNamespace, LogEventLevel.Warning)

             .WriteTo.Console()

             .WriteTo.File(Path.Combine(logDirectory, LogFileNamePattern),

                           rollingInterval: RollingInterval.Day,

                           retainedFileCountLimit: 7,

                           shared: true

                          )

             .CreateLogger();

const string McpEndpointPattern = "/mcp";

const string ServiceName = "DocRAG MCP";



var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();
builder.Services.AddSingleton(levelSwitch);

builder.Services.AddWindowsService(options =>

                                   {

                                       options.ServiceName = ServiceName;

                                   }

                                  );

builder.Services.AddSingleton(new DiagnosticTools.LogConfig(logDirectory));

builder.Services.AddSingleton<McpWarmupState>();

builder.Services.AddHostedService<McpWarmupService>();



// MongoDB

builder.Services.AddDocRagDatabase(builder.Configuration);



// Ollama configuration

builder.Services.Configure<OllamaSettings>(builder.Configuration.GetSection(OllamaSettings.SectionName));



// Ollama services

builder.Services.AddSingleton<OllamaBootstrapper>();

builder.Services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();

builder.Services.AddSingleton<IVectorSearchProvider, InMemoryBruteForceVectorSearch>();

// Re-ranker (toggleable at runtime via MCP tool)
builder.Services.AddSingleton<ToggleableReRanker>();
builder.Services.AddSingleton<IReRanker>(sp => sp.GetRequiredService<ToggleableReRanker>());



// Classification

builder.Services.AddSingleton<LlmClassifier>();



// Ingestion pipeline (so MCP can scrape on demand)

builder.Services.AddSingleton<GitHubRepoScraper>();

builder.Services.AddSingleton<PageCrawler>();

builder.Services.AddSingleton<CategoryAwareChunker>();

builder.Services.AddSingleton<IngestionOrchestrator>();

builder.Services.AddSingleton<ScrapeJobRunner>();

builder.Services.AddSingleton<IScrapeJobQueue>(sp =>

                                                   sp.GetRequiredService<ScrapeJobRunner>()

                                              );



// HTTP clients for package registry APIs

builder.Services.AddHttpClient(HttpClientNuGet)

       .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler

                                                     {

                                                         AutomaticDecompression = DecompressionMethods.All

                                                     }

                                          );

builder.Services.AddHttpClient(HttpClientNpm);

builder.Services.AddHttpClient(HttpClientPyPi);

builder.Services.AddHttpClient(HttpClientDocUrlProbe);



// Shared utilities

builder.Services.AddSingleton<CommonDocUrlPatterns>();

builder.Services.AddSingleton<PackageFilter>();



// NuGet ecosystem

builder.Services.AddSingleton<IProjectFileParser, NuGetProjectFileParser>();

builder.Services.AddSingleton<IPackageRegistryClient, NuGetRegistryClient>();

builder.Services.AddSingleton<IDocUrlResolver, NuGetDocUrlResolver>();



// npm ecosystem

builder.Services.AddSingleton<IProjectFileParser, NpmProjectFileParser>();

builder.Services.AddSingleton<IPackageRegistryClient, NpmRegistryClient>();

builder.Services.AddSingleton<IDocUrlResolver, NpmDocUrlResolver>();



// pip ecosystem

builder.Services.AddSingleton<IProjectFileParser, PipProjectFileParser>();

builder.Services.AddSingleton<IPackageRegistryClient, PyPiRegistryClient>();

builder.Services.AddSingleton<IDocUrlResolver, PipDocUrlResolver>();



// Dependency indexing orchestrator

builder.Services.AddSingleton<DependencyIndexer>();



// MCP server with Streamable HTTP transport

builder.Services

       .AddMcpServer(options =>

                     {

                         options.ServerInfo = new Implementation

                                                  {

                                                      Name = "DocRAG â€” Documentation RAG MCP Server",

                                                      Version = "0.3.0"

                                                  };

                     }

                    )

       .WithHttpTransport(t => t.Stateless = true)

       .WithToolsFromAssembly();



var app = builder.Build();

var startupSw = Stopwatch.StartNew();

app.Logger.LogInformation("[Startup] T+{Sec:F1}s â€” HTTP server starting", startupSw.Elapsed.TotalSeconds);



// Log first real request

var firstRequestLogged = false;

app.Use(async (context, next) =>

        {

            if (!firstRequestLogged)

            {

                firstRequestLogged = true;

                app.Logger.LogInformation("[Startup] T+{Sec:F1}s â€” First request: {Method} {Path}",

                                          startupSw.Elapsed.TotalSeconds,

                                          context.Request.Method,

                                          context.Request.Path

                                         );

            }



            await next();

        }

       );



// HTTPS redirection â€” enabled only when an HTTPS endpoint is configured

var httpsEndpointUrl = app.Configuration[KestrelHttpsEndpointKey];

if (!string.IsNullOrWhiteSpace(httpsEndpointUrl))

{

    app.UseHttpsRedirection();

}



// Health check

app.MapGet(HealthEndpointPath,

           (McpWarmupState warmupState) => Results.Ok(new

                                                          {

                                                              Status = HealthyStatus,

                                                              WarmupStatus = warmupState.Status,

                                                              WarmupPhase = warmupState.CurrentPhase,

                                                              WarmupError = warmupState.LastError

                                                          }

                                                     )

          );



// MCP endpoint

app.MapMcp(McpEndpointPattern);



app.Run();

