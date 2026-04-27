// PipIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Ecosystems.Common;
using DocRAG.Ingestion.Ecosystems.Pip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Tests.Integration;

/// <summary>
///     Integration tests for PyPI registry client and pip doc URL resolver.
///     These tests call live PyPI APIs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipIntegrationTests
{
    public PipIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("PyPI");
        services.AddHttpClient("DocUrlProbe");
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        mClient = new PyPiRegistryClient(factory);

        var commonLogger = Substitute.For<ILogger<CommonDocUrlPatterns>>();
        var common = new CommonDocUrlPatterns(factory, commonLogger);
        mResolver = new PipDocUrlResolver(common);
    }

    private readonly PyPiRegistryClient mClient;
    private readonly PipDocUrlResolver mResolver;

    [Fact]
    public async Task FetchMetadataReturnsDataForRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("requests", "2.31.0", ct);

        Assert.NotNull(metadata);
        Assert.Contains("readthedocs", metadata.DocumentationUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveDocUrlForRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("requests", "2.31.0", ct);
        Assert.NotNull(metadata);

        var resolution = await mResolver.ResolveAsync(metadata, ct);

        Assert.NotNull(resolution.DocUrl);
        Assert.Equal("registry", resolution.Source);
    }

    [Fact]
    public async Task FetchMetadataReturnsNullForNonexistentPackage()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = await mClient.FetchMetadataAsync("this-package-does-not-exist-12345", "1.0.0", ct);

        Assert.Null(metadata);
    }
}
