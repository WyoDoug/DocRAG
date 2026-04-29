// PackageFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Scanning;

#endregion

namespace SaddleRAG.Tests.Scanning;

public sealed class PackageFilterTests
{
    private readonly PackageFilter mFilter = new PackageFilter();

    [Theory]
    [InlineData("Microsoft.Extensions.Logging")]
    [InlineData("System.Text.Json")]
    public void FilterNuGetFrameworkPackageIsRemoved(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "nuget" }
                        };

        var result = mFilter.Filter(input);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("xunit")]
    [InlineData("Moq")]
    public void FilterNuGetTestPackageIsRemoved(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "nuget" }
                        };

        var result = mFilter.Filter(input);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Newtonsoft.Json")]
    [InlineData("OllamaSharp")]
    public void FilterNuGetRealPackageIsKept(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "nuget" }
                        };

        var result = mFilter.Filter(input);

        Assert.Single(result);
        Assert.Equal(packageId, result[index: 0].PackageId);
    }

    [Theory]
    [InlineData("@types/node")]
    [InlineData("eslint")]
    [InlineData("typescript")]
    public void FilterNpmToolingPackageIsRemoved(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "npm" }
                        };

        var result = mFilter.Filter(input);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("express")]
    [InlineData("react")]
    public void FilterNpmRealPackageIsKept(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "npm" }
                        };

        var result = mFilter.Filter(input);

        Assert.Single(result);
        Assert.Equal(packageId, result[index: 0].PackageId);
    }

    [Theory]
    [InlineData("setuptools")]
    [InlineData("pytest")]
    [InlineData("black")]
    public void FilterPipToolingPackageIsRemoved(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "pip" }
                        };

        var result = mFilter.Filter(input);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("flask")]
    public void FilterPipRealPackageIsKept(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "pip" }
                        };

        var result = mFilter.Filter(input);

        Assert.Single(result);
        Assert.Equal(packageId, result[index: 0].PackageId);
    }

    [Fact]
    public void FilterEmptyInputReturnsEmpty()
    {
        var result = mFilter.Filter([]);

        Assert.Empty(result);
    }

    [Fact]
    public void FilterUnknownEcosystemAllPackagesKept()
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency
                                { PackageId = "some-package", Version = "1.0.0", EcosystemId = "cargo" },
                            new PackageDependency
                                { PackageId = "another-package", Version = "2.0.0", EcosystemId = "cargo" }
                        };

        var result = mFilter.Filter(input);

        Assert.Equal(expected: 2, result.Count);
    }

    [Theory]
    [InlineData("microsoft.extensions.logging")]
    [InlineData("MICROSOFT.EXTENSIONS.LOGGING")]
    public void FilterNuGetFrameworkPackageCaseInsensitiveIsRemoved(string packageId)
    {
        var input = new List<PackageDependency>
                        {
                            new PackageDependency { PackageId = packageId, Version = "1.0.0", EcosystemId = "nuget" }
                        };

        var result = mFilter.Filter(input);

        Assert.Empty(result);
    }
}
