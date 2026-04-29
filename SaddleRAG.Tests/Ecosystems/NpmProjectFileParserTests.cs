// NpmProjectFileParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Ecosystems.Npm;

#endregion

namespace SaddleRAG.Tests.Ecosystems;

public sealed class NpmProjectFileParserTests
{
    private readonly NpmProjectFileParser mParser = new NpmProjectFileParser();

    private static string WriteTempFile(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void EcosystemIdIsNpm()
    {
        Assert.Equal("npm", mParser.EcosystemId);
    }

    [Fact]
    public async Task ParseAsyncDependenciesReturnsDependencies()
    {
        var json = """
                   {
                     "name": "my-app",
                     "dependencies": {
                       "express": "4.18.2",
                       "lodash": "4.17.21"
                     }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, result.Count);
            Assert.Contains(result, d => d.PackageId == "express" && d.Version == "4.18.2");
            Assert.Contains(result, d => d.PackageId == "lodash" && d.Version == "4.17.21");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncDevDependenciesReturnsDevDependencies()
    {
        var json = """
                   {
                     "name": "my-app",
                     "devDependencies": {
                       "jest": "29.0.0",
                       "webpack": "5.88.0"
                     }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, result.Count);
            Assert.Contains(result, d => d.PackageId == "jest" && d.Version == "29.0.0");
            Assert.Contains(result, d => d.PackageId == "webpack" && d.Version == "5.88.0");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncBothSectionsCombinesWithoutDuplicates()
    {
        var json = """
                   {
                     "dependencies": {
                       "express": "4.18.2"
                     },
                     "devDependencies": {
                       "jest": "29.0.0"
                     }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, result.Count);
            Assert.Contains(result, d => d.PackageId == "express");
            Assert.Contains(result, d => d.PackageId == "jest");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncMissingDependenciesReturnsOnlyDevDependencies()
    {
        var json = """
                   {
                     "name": "my-app",
                     "devDependencies": {
                       "jest": "29.0.0"
                     }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("jest", result[index: 0].PackageId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncMissingDevDependenciesReturnsOnlyDependencies()
    {
        var json = """
                   {
                     "name": "my-app",
                     "dependencies": {
                       "express": "4.18.2"
                     }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("express", result[index: 0].PackageId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncEmptyPackageJsonReturnsEmpty()
    {
        string path = WriteTempFile("{}");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Empty(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncVersionWithRangePrefixPreservesPrefix()
    {
        var json = """
                   {
                     "dependencies": {
                       "react": "^18.2.0",
                       "axios": "~1.4.0"
                     }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, result.Count);
            Assert.Contains(result, d => d.PackageId == "react" && d.Version == "^18.2.0");
            Assert.Contains(result, d => d.PackageId == "axios" && d.Version == "~1.4.0");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncAllPackagesEcosystemIdIsNpm()
    {
        var json = """
                   {
                     "dependencies": { "express": "4.18.2" },
                     "devDependencies": { "jest": "29.0.0" }
                   }
                   """;

        string path = WriteTempFile(json);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.All(result, d => Assert.Equal("npm", d.EcosystemId));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
