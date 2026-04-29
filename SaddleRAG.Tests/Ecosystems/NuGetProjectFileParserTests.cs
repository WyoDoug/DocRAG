// NuGetProjectFileParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Ecosystems.NuGet;

#endregion

namespace SaddleRAG.Tests.Ecosystems;

public sealed class NuGetProjectFileParserTests
{
    private readonly NuGetProjectFileParser mParser = new NuGetProjectFileParser();

    private static string WriteTempFile(string content, string extension)
    {
        string path = Path.ChangeExtension(Path.GetTempFileName(), extension);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void EcosystemIdIsNuGet()
    {
        Assert.Equal("nuget", mParser.EcosystemId);
    }

    [Fact]
    public async Task ParseAsyncCsprojWithVersionAttributeReturnsPackage()
    {
        var csproj = """
                     <Project Sdk="Microsoft.NET.Sdk">
                       <ItemGroup>
                         <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                       </ItemGroup>
                     </Project>
                     """;

        string path = WriteTempFile(csproj, ".csproj");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("Newtonsoft.Json", result[index: 0].PackageId);
            Assert.Equal("13.0.3", result[index: 0].Version);
            Assert.Equal("nuget", result[index: 0].EcosystemId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncCsprojWithVersionChildElementReturnsPackage()
    {
        var csproj = """
                     <Project Sdk="Microsoft.NET.Sdk">
                       <ItemGroup>
                         <PackageReference Include="OllamaSharp">
                           <Version>4.0.0</Version>
                         </PackageReference>
                       </ItemGroup>
                     </Project>
                     """;

        string path = WriteTempFile(csproj, ".csproj");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("OllamaSharp", result[index: 0].PackageId);
            Assert.Equal("4.0.0", result[index: 0].Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncCsprojWithEmptyIncludeSkipsEntry()
    {
        var csproj = """
                     <Project Sdk="Microsoft.NET.Sdk">
                       <ItemGroup>
                         <PackageReference Include="" Version="1.0.0" />
                         <PackageReference Include="Serilog" Version="3.1.1" />
                       </ItemGroup>
                     </Project>
                     """;

        string path = WriteTempFile(csproj, ".csproj");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("Serilog", result[index: 0].PackageId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncNonexistentFileThrowsFileNotFoundException()
    {
        string path = Path.Combine(Path.GetTempPath(), "does-not-exist.csproj");

        await Assert.ThrowsAsync<FileNotFoundException>(() => mParser.ParseAsync(path,
                                                                 TestContext.Current.CancellationToken
                                                            )
                                                       );
    }

    [Fact]
    public async Task ParseAsyncCsprojWithMultiplePackagesReturnsAll()
    {
        var csproj = """
                     <Project Sdk="Microsoft.NET.Sdk">
                       <ItemGroup>
                         <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                         <PackageReference Include="Serilog" Version="3.1.1" />
                         <PackageReference Include="Dapper" Version="2.1.35" />
                       </ItemGroup>
                     </Project>
                     """;

        string path = WriteTempFile(csproj, ".csproj");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 3, result.Count);
            Assert.Contains(result, d => d.PackageId == "Newtonsoft.Json");
            Assert.Contains(result, d => d.PackageId == "Serilog");
            Assert.Contains(result, d => d.PackageId == "Dapper");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsyncSlnPointingToTwoCsprojDeduplicatesSharedPackage()
    {
        var csprojA = """
                      <Project Sdk="Microsoft.NET.Sdk">
                        <ItemGroup>
                          <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                        </ItemGroup>
                      </Project>
                      """;

        var csprojB = """
                      <Project Sdk="Microsoft.NET.Sdk">
                        <ItemGroup>
                          <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                          <PackageReference Include="Serilog" Version="3.1.1" />
                        </ItemGroup>
                      </Project>
                      """;

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string pathA = Path.Combine(dir, "ProjectA.csproj");
        string pathB = Path.Combine(dir, "ProjectB.csproj");
        File.WriteAllText(pathA, csprojA);
        File.WriteAllText(pathB, csprojB);

        string sln =
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"ProjectA\", \"ProjectA.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\n" +
            "EndProject\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"ProjectB\", \"ProjectB.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\r\n" +
            "EndProject\r\n";

        string slnPath = Path.Combine(dir, "Test.sln");
        File.WriteAllText(slnPath, sln);

        try
        {
            var result = await mParser.ParseAsync(slnPath, TestContext.Current.CancellationToken);

            Assert.Equal(expected: 3, result.Count);
            Assert.Equal(expected: 2, result.Count(d => d.PackageId == "Newtonsoft.Json"));
            Assert.Single(result, d => d.PackageId == "Serilog");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseAsyncCsprojWithNoVersionAttributeUsesUnknown()
    {
        var csproj = """
                     <Project Sdk="Microsoft.NET.Sdk">
                       <ItemGroup>
                         <PackageReference Include="SomePackage" />
                       </ItemGroup>
                     </Project>
                     """;

        string path = WriteTempFile(csproj, ".csproj");
        try
        {
            var result = await mParser.ParseAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal("unknown", result[index: 0].Version);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
