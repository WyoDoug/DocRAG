// NuGetIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Net;
using SaddleRAG.Ingestion.Ecosystems.Common;
using SaddleRAG.Ingestion.Ecosystems.NuGet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tests.Integration;

/// <summary>
///     Integration tests for NuGet registry client and doc URL resolver.
///     These tests call live NuGet APIs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NuGetIntegrationTests
{
    public NuGetIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("NuGet")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                                                              {
                                                                  AutomaticDecompression = DecompressionMethods.All
                                                              }
                                                   );
        services.AddHttpClient("DocUrlProbe");
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var clientLogger = Substitute.For<ILogger<NuGetRegistryClient>>();
        mClient = new NuGetRegistryClient(factory, clientLogger);

        var commonLogger = Substitute.For<ILogger<CommonDocUrlPatterns>>();
        var common = new CommonDocUrlPatterns(factory, commonLogger);
        mResolver = new NuGetDocUrlResolver(common);
    }

    private readonly NuGetRegistryClient mClient;
    private readonly NuGetDocUrlResolver mResolver;

    [Fact]
    public async Task FetchMetadataReturnsDataForNewtonsoftJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("Newtonsoft.Json", "13.0.3", ct);

        Assert.NotNull(metadata);

        bool hasProjectUrl = !string.IsNullOrEmpty(metadata.ProjectUrl);
        bool hasRepoUrl = !string.IsNullOrEmpty(metadata.RepositoryUrl);
        Assert.True(hasProjectUrl || hasRepoUrl, "Expected ProjectUrl or RepositoryUrl to be non-empty");
        Assert.False(string.IsNullOrEmpty(metadata.Description), "Expected Description to be non-empty");
    }

    [Fact]
    public async Task ResolveDocUrlForNewtonsoftJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("Newtonsoft.Json", "13.0.3", ct);
        Assert.NotNull(metadata);

        var resolution = await mResolver.ResolveAsync(metadata, ct);

        Assert.NotNull(resolution.DocUrl);

        bool validSource = resolution.Source is "registry" or "github-repo";
        Assert.True(validSource, $"Expected Source to be 'registry' or 'github-repo', got '{resolution.Source}'");
    }

    [Fact]
    public async Task FetchMetadataReturnsNullForNonexistentPackage()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("ThisPackageDoesNotExist12345", "1.0.0", ct);

        Assert.Null(metadata);
    }
}
