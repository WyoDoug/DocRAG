// PipProjectFileParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Ecosystems.Pip;

#endregion

namespace SaddleRAG.Tests.Ecosystems;

public sealed class PipProjectFileParserTests
{
    private readonly PipProjectFileParser mParser = new PipProjectFileParser();

    private static (string Dir, string FilePath) WriteNamedTempFile(string fileName, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, fileName);
        File.WriteAllText(filePath, content);
        return (dir, filePath);
    }

    [Fact]
    public void EcosystemIdIsPip()
    {
        Assert.Equal("pip", mParser.EcosystemId);
    }

    [Fact]
    public async Task ParseAsyncRequirementsTxtPinSpecifierParsesPackage()
    {
        var content = "requests==2.31.0\n";
        (string dir, string path) = WriteNamedTempFile("requirements.txt", content);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("requests", result[index: 0].PackageId);
            Assert.Equal("2.31.0", result[index: 0].Version);
            Assert.Equal("pip", result[index: 0].EcosystemId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("flask>=2.3.0", "flask", "2.3.0")]
    [InlineData("numpy~=1.24.0", "numpy", "1.24.0")]
    public async Task ParseAsyncRequirementsTxtOtherSpecifiersParsesPackage(
        string line,
        string expectedId,
        string expectedVersion)
    {
        (string dir, string path) = WriteNamedTempFile("requirements.txt", line + "\n");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal(expectedId, result[index: 0].PackageId);
            Assert.Equal(expectedVersion, result[index: 0].Version);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncRequirementsTxtCommentLinesAreSkipped()
    {
        var content = """
                      # This is a comment
                      requests==2.31.0
                      # Another comment
                      """;

        (string dir, string path) = WriteNamedTempFile("requirements.txt", content);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("requests", result[index: 0].PackageId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncRequirementsTxtFlagLinesAreSkipped()
    {
        var content = """
                      -r base.txt
                      -i https://pypi.org/simple
                      requests==2.31.0
                      """;

        (string dir, string path) = WriteNamedTempFile("requirements.txt", content);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("requests", result[index: 0].PackageId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncRequirementsTxtUnpinnedPackageParsesWithEmptyVersion()
    {
        var content = "requests\n";
        (string dir, string path) = WriteNamedTempFile("requirements.txt", content);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("requests", result[index: 0].PackageId);
            Assert.Equal(string.Empty, result[index: 0].Version);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncPyprojectTomlProjectDependenciesReturnsDependencies()
    {
        var content = """
                      [build-system]
                      requires = ["setuptools"]

                      [project]
                      name = "myapp"
                      dependencies = [
                        "requests==2.31.0",
                        "flask>=2.3.0",
                      ]
                      """;

        (string dir, string path) = WriteNamedTempFile("pyproject.toml", content);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 2, result.Count);
            Assert.Contains(result, d => d.PackageId == "requests" && d.Version == "2.31.0");
            Assert.Contains(result, d => d.PackageId == "flask" && d.Version == "2.3.0");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncRequirementsTxtEmptyFileReturnsEmpty()
    {
        (string dir, string path) = WriteNamedTempFile("requirements.txt", string.Empty);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncPyprojectTomlEmptyFileReturnsEmpty()
    {
        (string dir, string path) = WriteNamedTempFile("pyproject.toml", string.Empty);
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
