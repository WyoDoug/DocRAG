// DependencyIndexerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DocRAG.Tests.Scanning;

public sealed class DependencyIndexerTests
{
    public DependencyIndexerTests()
    {
        mNugetParser = Substitute.For<IProjectFileParser>();
        mNugetParser.EcosystemId.Returns("nuget");
        mNugetParser.FilePatterns.Returns(new List<string> { "*.csproj" });

        mNpmParser = Substitute.For<IProjectFileParser>();
        mNpmParser.EcosystemId.Returns("npm");
        mNpmParser.FilePatterns.Returns(new List<string> { "package.json" });

        mRegistryClient = Substitute.For<IPackageRegistryClient>();
        mRegistryClient.EcosystemId.Returns("nuget");

        mUrlResolver = Substitute.For<IDocUrlResolver>();
        mUrlResolver.EcosystemId.Returns("nuget");

        mLibraryRepo = Substitute.For<ILibraryRepository>();
        mLibraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                    .Returns(new List<LibraryRecord>());

        mJobQueue = Substitute.For<IScrapeJobQueue>();

        var dbSettings = Options.Create(new DocRagDbSettings());
        var contextFactory = new DocRagDbContextFactory(dbSettings);
        mRepoFactory = Substitute.For<RepositoryFactory>(contextFactory);
        mRepoFactory.GetLibraryRepository(Arg.Any<string?>()).Returns(mLibraryRepo);

        mLogger = Substitute.For<ILogger<DependencyIndexer>>();
    }

    private readonly IScrapeJobQueue mJobQueue;
    private readonly ILibraryRepository mLibraryRepo;
    private readonly ILogger<DependencyIndexer> mLogger;
    private readonly IProjectFileParser mNpmParser;

    private readonly IProjectFileParser mNugetParser;
    private readonly IPackageRegistryClient mRegistryClient;
    private readonly RepositoryFactory mRepoFactory;
    private readonly IDocUrlResolver mUrlResolver;

    private DependencyIndexer BuildIndexer(IEnumerable<IProjectFileParser>? parsers = null,
                                           IEnumerable<IPackageRegistryClient>? registryClients = null,
                                           IEnumerable<IDocUrlResolver>? urlResolvers = null)
    {
        var indexer = new DependencyIndexer(parsers ?? new[] { mNugetParser },
                                            registryClients ?? new[] { mRegistryClient },
                                            urlResolvers ?? new[] { mUrlResolver },
                                            new PackageFilter(),
                                            mJobQueue,
                                            mRepoFactory,
                                            mLogger
                                           );
        return indexer;
    }

    [Fact]
    public async Task IndexProjectAsyncWithCsprojFileParsesAndReturnsDependencies()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency
                               { PackageId = "Newtonsoft.Json", Version = "13.0.3", EcosystemId = "nuget" },
                           new PackageDependency { PackageId = "Serilog", Version = "3.1.1", EcosystemId = "nuget" },
                           new PackageDependency { PackageId = "Polly", Version = "8.0.0", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        ConfigureFullResolveChain("https://docs.example.com");
        mJobQueue.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns("job-1");

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 3, report.TotalDependencies);
    }

    [Fact]
    public async Task IndexProjectAsyncFilteredPackagesReportedAsFilteredOut()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency
                               { PackageId = "Microsoft.Extensions.Logging", Version = "8.0.0", EcosystemId = "nuget" },
                           new PackageDependency
                               { PackageId = "Newtonsoft.Json", Version = "13.0.3", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        ConfigureFullResolveChain("https://docs.example.com");
        mJobQueue.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns("job-1");

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 1, report.FilteredOut);
        Assert.Contains(report.Packages,
                        p =>
                            p.PackageId == "Microsoft.Extensions.Logging" && p.Status == "filtered"
                       );
    }

    [Fact]
    public async Task IndexProjectAsyncCachedPackageReportedAsCached()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency
                               { PackageId = "Newtonsoft.Json", Version = "13.0.3", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        var cached = new LibraryRecord
                         {
                             Id = "newtonsoft.json",
                             Name = "Newtonsoft.Json",
                             Hint = "JSON framework",
                             CurrentVersion = "13.0.3",
                             AllVersions = new List<string> { "13.0.3" }
                         };
        mLibraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                    .Returns(new List<LibraryRecord> { cached });

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 1, report.AlreadyCached);
        Assert.Contains(report.Packages,
                        p =>
                            p.PackageId == "Newtonsoft.Json" && p.Status == "cached"
                       );
    }

    [Fact]
    public async Task IndexProjectAsyncCachedDifferentVersionReportedCorrectly()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency
                               { PackageId = "Newtonsoft.Json", Version = "14.0.0", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        var cached = new LibraryRecord
                         {
                             Id = "newtonsoft.json",
                             Name = "Newtonsoft.Json",
                             Hint = "JSON framework",
                             CurrentVersion = "13.0.3",
                             AllVersions = new List<string> { "13.0.3" }
                         };
        mLibraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                    .Returns(new List<LibraryRecord> { cached });

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 1, report.CachedDifferentVersion);
        Assert.Contains(report.Packages,
                        p =>
                            p.PackageId == "Newtonsoft.Json" &&
                            p.Status == "cached-different-version" &&
                            p.CachedVersion == "13.0.3"
                       );
    }

    [Fact]
    public async Task IndexProjectAsyncResolvedAndQueuedReturnsJobId()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency { PackageId = "Serilog", Version = "3.1.1", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        ConfigureFullResolveChain("https://serilog.net/docs");
        mJobQueue.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns("job-42");

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 1, report.NewlyQueued);
        Assert.Contains(report.Packages,
                        p =>
                            p.PackageId == "Serilog" &&
                            p.Status == "queued" &&
                            p.JobId == "job-42" &&
                            p.DocUrl == "https://serilog.net/docs"
                       );
    }

    [Fact]
    public async Task IndexProjectAsyncRegistryReturnsNullReportsResolutionFailed()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency { PackageId = "ObscureLib", Version = "1.0.0", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        mRegistryClient.FetchMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns((PackageMetadata?) null);

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 1, report.ResolutionFailed);
        Assert.Contains(report.Packages,
                        p =>
                            p.PackageId == "ObscureLib" && p.Status == "failed"
                       );
    }

    [Fact]
    public async Task IndexProjectAsyncResolverReturnsNoUrlReportsNoDocumentationFound()
    {
        // Arrange
        var deps = new List<PackageDependency>
                       {
                           new PackageDependency { PackageId = "SomeLib", Version = "2.0.0", EcosystemId = "nuget" }
                       };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(deps);

        mRegistryClient.FetchMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns(new PackageMetadata
                                    {
                                        PackageId = "SomeLib",
                                        Version = "2.0.0",
                                        EcosystemId = "nuget"
                                    }
                               );

        mUrlResolver.ResolveAsync(Arg.Any<PackageMetadata>(), Arg.Any<CancellationToken>())
                    .Returns(new DocUrlResolution
                                 {
                                     DocUrl = null,
                                     Source = "none",
                                     Confidence = ScanConfidence.Low
                                 }
                            );

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 1, report.ResolutionFailed);
        Assert.Contains(report.Packages,
                        p =>
                            p.PackageId == "SomeLib" && p.Status == "failed"
                       );
    }

    [Fact]
    public async Task IndexProjectAsyncEmptyProjectReturnsZeroCounts()
    {
        // Arrange
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new List<PackageDependency>());

        var indexer = BuildIndexer();

        // Act
        var report = await indexer.IndexProjectAsync(smTestCsprojPath, profile: null, CancellationToken.None);

        // Assert
        Assert.Equal(expected: 0, report.TotalDependencies);
        Assert.Equal(expected: 0, report.FilteredOut);
        Assert.Equal(expected: 0, report.AlreadyCached);
        Assert.Equal(expected: 0, report.NewlyQueued);
        Assert.Equal(expected: 0, report.ResolutionFailed);
        Assert.Empty(report.Packages);
    }

    [Fact]
    public async Task IndexProjectAsyncMultipleEcosystemsAllProcessed()
    {
        // Arrange
        var nugetDeps = new List<PackageDependency>
                            {
                                new PackageDependency
                                    { PackageId = "Newtonsoft.Json", Version = "13.0.3", EcosystemId = "nuget" }
                            };
        mNugetParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(nugetDeps);

        var npmDeps = new List<PackageDependency>
                          {
                              new PackageDependency { PackageId = "lodash", Version = "4.17.21", EcosystemId = "npm" }
                          };
        mNpmParser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(npmDeps);

        var npmRegistryClient = Substitute.For<IPackageRegistryClient>();
        npmRegistryClient.EcosystemId.Returns("npm");
        npmRegistryClient.FetchMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns(new PackageMetadata
                                      {
                                          PackageId = "lodash",
                                          Version = "4.17.21",
                                          EcosystemId = "npm"
                                      }
                                 );

        var npmResolver = Substitute.For<IDocUrlResolver>();
        npmResolver.EcosystemId.Returns("npm");
        npmResolver.ResolveAsync(Arg.Any<PackageMetadata>(), Arg.Any<CancellationToken>())
                   .Returns(new DocUrlResolution
                                {
                                    DocUrl = "https://lodash.com/docs",
                                    Source = "registry",
                                    Confidence = ScanConfidence.High
                                }
                           );

        ConfigureFullResolveChain("https://www.newtonsoft.com/json");
        mJobQueue.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns("job-nuget", "job-npm");

        // Use a real directory that contains both a .csproj and a fake package.json.
        // Since we can't guarantee package.json exists, use the .csproj path directly
        // and invoke with two parsers. The directory-based detection needs real files,
        // so we test with the project file path and verify both parsers contribute
        // by overriding DetectProjectFiles behavior.
        //
        // Because DetectProjectFiles uses the file system, this test uses the
        // project directory which has a real .csproj. For npm, we create a temporary
        // package.json beside it so both parsers match.
        string testDir = Path.GetDirectoryName(smTestCsprojPath) ?? string.Empty;
        string fakePackageJson = Path.Combine(testDir, "package.json");
        var createdFile = false;
        if (!File.Exists(fakePackageJson))
        {
            File.WriteAllText(fakePackageJson, "{}");
            createdFile = true;
        }

        try
        {
            var indexer = BuildIndexer(new[] { mNugetParser, mNpmParser },
                                       new[] { mRegistryClient, npmRegistryClient },
                                       new[] { mUrlResolver, npmResolver }
                                      );

            // Act
            var report = await indexer.IndexProjectAsync(testDir, profile: null, CancellationToken.None);

            // Assert
            Assert.Equal(expected: 2, report.TotalDependencies);
            Assert.Equal(expected: 2, report.NewlyQueued);
            Assert.Contains(report.Packages, p => p.PackageId == "Newtonsoft.Json");
            Assert.Contains(report.Packages, p => p.PackageId == "lodash");
        }
        finally
        {
            if (createdFile && File.Exists(fakePackageJson))
                File.Delete(fakePackageJson);
        }
    }

    private void ConfigureFullResolveChain(string docUrl)
    {
        mRegistryClient.FetchMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns(callInfo => new PackageMetadata
                                                {
                                                    PackageId = (string) callInfo[index: 0],
                                                    Version = (string) callInfo[index: 1],
                                                    EcosystemId = "nuget"
                                                }
                               );

        mUrlResolver.ResolveAsync(Arg.Any<PackageMetadata>(), Arg.Any<CancellationToken>())
                    .Returns(new DocUrlResolution
                                 {
                                     DocUrl = docUrl,
                                     Source = "registry",
                                     Confidence = ScanConfidence.High
                                 }
                            );
    }

    // Path to a real .csproj that exists on disk so DetectProjectFiles finds it.
    private static readonly string smTestCsprojPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DocRAG.Tests.csproj"));

    private static readonly string smTestPackageJsonPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fake-package.json"));
}
