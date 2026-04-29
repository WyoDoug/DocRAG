// NpmIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Ecosystems.Common;
using SaddleRAG.Ingestion.Ecosystems.Npm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tests.Integration;

/// <summary>
///     Integration tests for npm registry client and doc URL resolver.
///     These tests call live npm APIs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NpmIntegrationTests
{
    public NpmIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("npm");
        services.AddHttpClient("DocUrlProbe");
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        mClient = new NpmRegistryClient(factory);

        var commonLogger = Substitute.For<ILogger<CommonDocUrlPatterns>>();
        var common = new CommonDocUrlPatterns(factory, commonLogger);
        mResolver = new NpmDocUrlResolver(common);
    }

    private readonly NpmRegistryClient mClient;
    private readonly NpmDocUrlResolver mResolver;

    [Fact]
    public async Task FetchMetadataReturnsDataForExpress()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("express", "4.18.2", ct);

        Assert.NotNull(metadata);
        Assert.Contains("expressjs", metadata.ProjectUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveDocUrlForExpress()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("express", "4.18.2", ct);
        Assert.NotNull(metadata);

        var resolution = await mResolver.ResolveAsync(metadata, ct);

        Assert.NotNull(resolution.DocUrl);
    }

    [Fact]
    public async Task FetchMetadataReturnsNullForNonexistentPackage()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("this-package-does-not-exist-12345", "1.0.0", ct);

        Assert.Null(metadata);
    }
}
