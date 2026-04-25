// // LibraryProfileServiceTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Recon;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

#endregion

namespace DocRAG.Tests.Recon;

public sealed class LibraryProfileServiceTests
{
    [Fact]
    public void BuildPopulatesIdAndCreatedUtc()
    {
        var profile = LibraryProfileService.Build("aerotech-aeroscript",
                                                  "2025.3",
                                                  ["AeroScript"],
                                                  new CasingConventions { Types = "PascalCase" },
                                                  ["."],
                                                  ["Foo()"],
                                                  ["MoveLinear", "AxisStatus"],
                                                  canonicalInventoryUrl: null,
                                                  confidence: 0.85f,
                                                  source: "calling-llm"
                                                 );

        Assert.Equal("aerotech-aeroscript/2025.3", profile.Id);
        Assert.Equal("aerotech-aeroscript", profile.LibraryId);
        Assert.Equal("2025.3", profile.Version);
        Assert.Equal(LibraryProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal(0.85f, profile.Confidence);
        Assert.Equal("calling-llm", profile.Source);
        Assert.True((DateTime.UtcNow - profile.CreatedUtc).TotalSeconds < 5);
    }

    [Fact]
    public void BuildThrowsOnEmptyLibraryId()
    {
        Assert.Throws<ArgumentException>(() =>
                                             LibraryProfileService.Build(libraryId: string.Empty,
                                                                         "2025.3",
                                                                         [],
                                                                         new CasingConventions(),
                                                                         [],
                                                                         [],
                                                                         [],
                                                                         canonicalInventoryUrl: null,
                                                                         confidence: 0.5f,
                                                                         source: "calling-llm"
                                                                        )
                                        );
    }

    [Fact]
    public void ComputeHashIsStableAcrossEquivalentProfiles()
    {
        var a = MakeProfile(["a", "b"], ["MoveLinear", "AxisStatus"], confidence: 0.5f, source: "src1");
        var b = MakeProfile(["b", "a"], ["AxisStatus", "MoveLinear"], confidence: 0.9f, source: "src2");

        var hashA = LibraryProfileService.ComputeHash(a);
        var hashB = LibraryProfileService.ComputeHash(b);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void ComputeHashChangesWhenStructuralContentChanges()
    {
        var a = MakeProfile(["AeroScript"], ["MoveLinear"], confidence: 0.5f, source: "src");
        var b = MakeProfile(["AeroScript"], ["MoveLinear", "AxisStatus"], confidence: 0.5f, source: "src");

        var hashA = LibraryProfileService.ComputeHash(a);
        var hashB = LibraryProfileService.ComputeHash(b);

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public async Task SaveAsyncPersistsViaRepository()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
        var profile = MakeProfile(["AeroScript"], ["MoveLinear"], confidence: 0.7f, source: "calling-llm");

        var result = await service.SaveAsync(repo, profile, TestContext.Current.CancellationToken);

        Assert.Equal(profile.Id, result.Id);
        await repo.Received(1).UpsertAsync(Arg.Any<LibraryProfile>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsyncRejectsConfidenceOutOfRange()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
        var bad = MakeProfile(["AeroScript"], ["MoveLinear"], confidence: 1.5f, source: "calling-llm");

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(repo, bad, TestContext.Current.CancellationToken));
        await repo.DidNotReceive().UpsertAsync(Arg.Any<LibraryProfile>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsyncNormalizesIdToCanonicalFormat()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);

        var built = MakeProfile(["AeroScript"], ["MoveLinear"], confidence: 0.7f, source: "calling-llm");
        var withWrongId = built with { Id = "wrong-id" };

        var saved = await service.SaveAsync(repo, withWrongId, TestContext.Current.CancellationToken);

        Assert.Equal("aerotech-aeroscript/2025.3", saved.Id);
    }

    private static LibraryProfile MakeProfile(IReadOnlyList<string> languages,
                                              IReadOnlyList<string> likely,
                                              float confidence,
                                              string source)
    {
        var result = LibraryProfileService.Build("aerotech-aeroscript",
                                                 "2025.3",
                                                 languages,
                                                 new CasingConventions { Types = "PascalCase" },
                                                 ["."],
                                                 ["Foo()"],
                                                 likely,
                                                 canonicalInventoryUrl: null,
                                                 confidence,
                                                 source
                                                );
        return result;
    }
}
