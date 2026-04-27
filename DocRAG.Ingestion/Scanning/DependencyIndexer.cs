// DependencyIndexer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Ingestion.Scanning;

/// <summary>
///     Orchestrates the full dependency indexing pipeline: detect project files,
///     parse dependencies, filter, check cache, resolve documentation URLs, and
///     enqueue scrape jobs for uncached packages.
/// </summary>
public class DependencyIndexer
{
    public DependencyIndexer(IEnumerable<IProjectFileParser> parsers,
                             IEnumerable<IPackageRegistryClient> registryClients,
                             IEnumerable<IDocUrlResolver> urlResolvers,
                             PackageFilter packageFilter,
                             IScrapeJobQueue jobRunner,
                             RepositoryFactory repositoryFactory,
                             ILogger<DependencyIndexer> logger)
    {
        mParsers = parsers.ToList();
        mRegistryClients = registryClients.ToList();
        mUrlResolvers = urlResolvers.ToList();
        mPackageFilter = packageFilter;
        mJobRunner = jobRunner;
        mRepositoryFactory = repositoryFactory;
        mLogger = logger;
    }

    private readonly IScrapeJobQueue mJobRunner;
    private readonly ILogger<DependencyIndexer> mLogger;
    private readonly PackageFilter mPackageFilter;

    private readonly IReadOnlyList<IProjectFileParser> mParsers;
    private readonly IReadOnlyList<IPackageRegistryClient> mRegistryClients;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IReadOnlyList<IDocUrlResolver> mUrlResolvers;

    /// <summary>
    ///     Runs the full scanâ†’resolveâ†’scrape pipeline for the given project path.
    ///     If <paramref name="projectPath" /> is a file, it is parsed directly.
    ///     If it is a directory, all recognized project files beneath it are parsed.
    /// </summary>
    public async Task<DependencyIndexReport> IndexProjectAsync(string projectPath,
                                                               string? profile,
                                                               CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        var detectedFiles = DetectProjectFiles(projectPath);

        mLogger.LogInformation("Detected {Count} project file(s) under {Path}",
                               detectedFiles.Count,
                               projectPath
                              );

        var allDependencies = new List<PackageDependency>();
        foreach((string filePath, var parser) in detectedFiles)
        {
            var parsed = await parser.ParseAsync(filePath, ct);
            allDependencies.AddRange(parsed);
        }

        var deduplicated = allDependencies
                           .GroupBy(d => (d.PackageId.ToLower(), d.EcosystemId))
                           .Select(g => g.First())
                           .ToList();

        int totalCount = deduplicated.Count;

        var filtered = mPackageFilter.Filter(deduplicated);
        int filteredOutCount = totalCount - filtered.Count;

        mLogger.LogInformation("Found {Total} dependencies, {FilteredOut} filtered out, {Remaining} to process",
                               totalCount,
                               filteredOutCount,
                               filtered.Count
                              );

        var libraryRepo = mRepositoryFactory.GetLibraryRepository(profile);
        var allLibraries = await libraryRepo.GetAllLibrariesAsync(ct);
        var libraryLookup = allLibraries.ToDictionary(l => l.Id, l => l);

        var statusList = new List<PackageIndexStatus>();
        foreach(var dep in filtered)
        {
            var status = await ProcessPackageAsync(dep, libraryLookup, profile, ct);
            statusList.Add(status);
        }

        var filteredStatuses = deduplicated
                               .Where(d => !filtered.Any(f => f.PackageId == d.PackageId &&
                                                              f.EcosystemId == d.EcosystemId
                                                        )
                                     )
                               .Select(d => new PackageIndexStatus
                                                {
                                                    PackageId = d.PackageId,
                                                    Version = d.Version,
                                                    EcosystemId = d.EcosystemId,
                                                    Status = StatusFiltered
                                                }
                                      );

        var allStatuses = statusList.Concat(filteredStatuses).ToList();

        var report = new DependencyIndexReport
                         {
                             ProjectPath = projectPath,
                             TotalDependencies = totalCount,
                             FilteredOut = filteredOutCount,
                             AlreadyCached = allStatuses.Count(s => s.Status == StatusCached),
                             CachedDifferentVersion = allStatuses.Count(s => s.Status == StatusCachedDifferentVersion),
                             NewlyQueued = allStatuses.Count(s => s.Status == StatusQueued),
                             ResolutionFailed = allStatuses.Count(s => s.Status == StatusFailed),
                             Packages = allStatuses
                         };

        return report;
    }

    /// <summary>
    ///     Walks <paramref name="path" /> to find all project files that a registered
    ///     parser can handle, skipping ignored directories. If <paramref name="path" />
    ///     is a file, it is matched directly.
    /// </summary>
    public IReadOnlyList<(string FilePath, IProjectFileParser Parser)> DetectProjectFiles(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var results = new List<(string, IProjectFileParser)>();

        switch(true)
        {
            case true when File.Exists(path):
                var matchingParser = mParsers.FirstOrDefault(p =>
                                                                 p.FilePatterns.Any(pattern =>
                                                                         MatchesGlob(path, pattern)
                                                                     )
                                                            );
                if (matchingParser is not null)
                    results.Add((path, matchingParser));
                break;
            case true when Directory.Exists(path):
                CollectProjectFiles(path, results);
                break;
        }

        return results;
    }

    private void CollectProjectFiles(string directory,
                                     List<(string FilePath, IProjectFileParser Parser)> results)
    {
        var dirInfo = new DirectoryInfo(directory);

        foreach(var file in dirInfo.GetFiles())
        {
            var matchingParser = mParsers.FirstOrDefault(p =>
                                                             p.FilePatterns.Any(pattern =>
                                                                                    MatchesGlob(file.FullName,
                                                                                             pattern
                                                                                        )
                                                                               )
                                                        );

            if (matchingParser is not null)
                results.Add((file.FullName, matchingParser));
        }

        foreach(var subDir in dirInfo.GetDirectories())
        {
            bool shouldSkip = smSkippedDirectories.Contains(subDir.Name, StringComparer.OrdinalIgnoreCase);

            if (!shouldSkip)
                CollectProjectFiles(subDir.FullName, results);
        }
    }

    private async Task<PackageIndexStatus> ProcessPackageAsync(PackageDependency dep,
                                                               Dictionary<string, LibraryRecord> libraryLookup,
                                                               string? profile,
                                                               CancellationToken ct)
    {
        string libraryId = dep.PackageId.ToLower();

        PackageIndexStatus result;
        if (libraryLookup.TryGetValue(libraryId, out var existing))
        {
            result = existing.AllVersions.Contains(dep.Version)
                         ? new PackageIndexStatus
                               {
                                   PackageId = dep.PackageId,
                                   Version = dep.Version,
                                   EcosystemId = dep.EcosystemId,
                                   Status = StatusCached
                               }
                         : new PackageIndexStatus
                               {
                                   PackageId = dep.PackageId,
                                   Version = dep.Version,
                                   EcosystemId = dep.EcosystemId,
                                   Status = StatusCachedDifferentVersion,
                                   CachedVersion = existing.CurrentVersion
                               };
        }
        else
            result = await ResolveAndQueueAsync(dep, profile, ct);

        return result;
    }

    private async Task<PackageIndexStatus> ResolveAndQueueAsync(PackageDependency dep,
                                                                string? profile,
                                                                CancellationToken ct)
    {
        var client = mRegistryClients.FirstOrDefault(c =>
                                                         string.Equals(c.EcosystemId,
                                                                       dep.EcosystemId,
                                                                       StringComparison.OrdinalIgnoreCase
                                                                      )
                                                    );

        PackageIndexStatus result;
        if (client is null)
        {
            mLogger.LogWarning("No registry client found for ecosystem {EcosystemId} (package {PackageId})",
                               dep.EcosystemId,
                               dep.PackageId
                              );

            result = new PackageIndexStatus
                         {
                             PackageId = dep.PackageId,
                             Version = dep.Version,
                             EcosystemId = dep.EcosystemId,
                             Status = StatusFailed,
                             ErrorMessage = $"No registry client registered for ecosystem '{dep.EcosystemId}'"
                         };
        }
        else
            result = await FetchAndQueueAsync(dep, client, profile, ct);

        return result;
    }

    private async Task<PackageIndexStatus> FetchAndQueueAsync(PackageDependency dep,
                                                              IPackageRegistryClient client,
                                                              string? profile,
                                                              CancellationToken ct)
    {
        PackageIndexStatus result;
        try
        {
            var metadata = await client.FetchMetadataAsync(dep.PackageId, dep.Version, ct);

            result = metadata is null
                         ? new PackageIndexStatus
                               {
                                   PackageId = dep.PackageId,
                                   Version = dep.Version,
                                   EcosystemId = dep.EcosystemId,
                                   Status = StatusFailed,
                                   ErrorMessage = $"Package '{dep.PackageId}' v{dep.Version} not found in registry"
                               }
                         : await QueueOrReportAsync(dep, metadata, profile, ct);
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex,
                             "Failed to fetch metadata for {PackageId} v{Version}",
                             dep.PackageId,
                             dep.Version
                            );

            result = new PackageIndexStatus
                         {
                             PackageId = dep.PackageId,
                             Version = dep.Version,
                             EcosystemId = dep.EcosystemId,
                             Status = StatusFailed,
                             ErrorMessage = ex.Message
                         };
        }

        return result;
    }

    private async Task<PackageIndexStatus> QueueOrReportAsync(PackageDependency dep,
                                                              PackageMetadata metadata,
                                                              string? profile,
                                                              CancellationToken ct)
    {
        var resolver = mUrlResolvers.FirstOrDefault(r =>
                                                        string.Equals(r.EcosystemId,
                                                                      dep.EcosystemId,
                                                                      StringComparison.OrdinalIgnoreCase
                                                                     )
                                                   );

        PackageIndexStatus result;
        if (resolver is null)
        {
            mLogger.LogWarning("No URL resolver found for ecosystem {EcosystemId} (package {PackageId})",
                               dep.EcosystemId,
                               dep.PackageId
                              );

            result = new PackageIndexStatus
                         {
                             PackageId = dep.PackageId,
                             Version = dep.Version,
                             EcosystemId = dep.EcosystemId,
                             Status = StatusFailed,
                             ErrorMessage = $"No URL resolver registered for ecosystem '{dep.EcosystemId}'"
                         };
        }
        else
            result = await ResolveUrlAndQueueAsync(dep, metadata, resolver, profile, ct);

        return result;
    }

    private async Task<PackageIndexStatus> ResolveUrlAndQueueAsync(PackageDependency dep,
                                                                   PackageMetadata metadata,
                                                                   IDocUrlResolver resolver,
                                                                   string? profile,
                                                                   CancellationToken ct)
    {
        PackageIndexStatus result;
        try
        {
            var resolution = await resolver.ResolveAsync(metadata, ct);

            result = string.IsNullOrEmpty(resolution.DocUrl)
                         ? new PackageIndexStatus
                               {
                                   PackageId = dep.PackageId,
                                   Version = dep.Version,
                                   EcosystemId = dep.EcosystemId,
                                   Status = StatusFailed,
                                   ErrorMessage =
                                       $"URL resolver returned no documentation URL (source: {resolution.Source})"
                               }
                         : await EnqueueJobAsync(dep, resolution.DocUrl, profile, ct);
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex,
                             "Failed to resolve documentation URL for {PackageId} v{Version}",
                             dep.PackageId,
                             dep.Version
                            );

            result = new PackageIndexStatus
                         {
                             PackageId = dep.PackageId,
                             Version = dep.Version,
                             EcosystemId = dep.EcosystemId,
                             Status = StatusFailed,
                             ErrorMessage = ex.Message
                         };
        }

        return result;
    }

    private async Task<PackageIndexStatus> EnqueueJobAsync(PackageDependency dep,
                                                           string docUrl,
                                                           string? profile,
                                                           CancellationToken ct)
    {
        string libraryId = dep.PackageId.ToLower();
        var job = ScrapeJobFactory.CreateFromUrl(docUrl, libraryId, dep.Version);
        string jobId = await mJobRunner.QueueAsync(job, profile, ct);

        mLogger.LogInformation("Queued scrape job {JobId} for {PackageId} v{Version} â†’ {DocUrl}",
                               jobId,
                               dep.PackageId,
                               dep.Version,
                               docUrl
                              );

        var result = new PackageIndexStatus
                         {
                             PackageId = dep.PackageId,
                             Version = dep.Version,
                             EcosystemId = dep.EcosystemId,
                             Status = StatusQueued,
                             DocUrl = docUrl,
                             JobId = jobId
                         };

        return result;
    }

    private static bool MatchesGlob(string filePath, string pattern)
    {
        string fileName = Path.GetFileName(filePath);
        string patternFileName = Path.GetFileName(pattern);

        bool result = patternFileName.Contains(value: '*')
                          ? MatchesWildcard(fileName, patternFileName)
                          : string.Equals(fileName, patternFileName, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    private static bool MatchesWildcard(string name, string pattern)
    {
        string[] parts = pattern.Split(separator: '*');
        var result = true;
        var pos = 0;

        foreach(string part in parts)
        {
            if (part.Length > 0)
            {
                int idx = name.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    result = false;
                    break;
                }

                pos = idx + part.Length;
            }
        }

        return result;
    }

    private const string StatusCached = "cached";
    private const string StatusCachedDifferentVersion = "cached-different-version";
    private const string StatusQueued = "queued";
    private const string StatusFailed = "failed";
    private const string StatusFiltered = "filtered";

    private const string SkipNodeModules = "node_modules";
    private const string SkipBin = "bin";
    private const string SkipObj = "obj";
    private const string SkipGit = ".git";
    private const string SkipVs = ".vs";
    private const string SkipPackages = "packages";

    private static readonly string[] smSkippedDirectories =
        [
            SkipNodeModules, SkipBin, SkipObj, SkipGit, SkipVs, SkipPackages
        ];
}
