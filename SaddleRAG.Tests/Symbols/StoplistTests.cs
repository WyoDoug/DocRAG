// StoplistTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Tests.Symbols;

public sealed class StoplistTests
{
    [Fact]
    public void MatchReturnsGlobalForUniversalStoplistHit()
    {
        var profile = MakeProfile([]);

        var match = Stoplist.Match("the", profile);

        Assert.Equal(StoplistMatch.Global, match);
    }

    [Fact]
    public void MatchReturnsLibraryForProfileStoplistHit()
    {
        var profile = MakeProfile(["along"]);

        var match = Stoplist.Match("along", profile);

        Assert.Equal(StoplistMatch.Library, match);
    }

    [Fact]
    public void MatchReturnsNoneForNonStoplistedToken()
    {
        var profile = MakeProfile([]);

        var match = Stoplist.Match("MoveLinear", profile);

        Assert.Equal(StoplistMatch.None, match);
    }

    [Fact]
    public void MatchIsCaseInsensitiveOnLibraryStoplist()
    {
        var profile = MakeProfile(["Along"]);

        var match = Stoplist.Match("along", profile);

        Assert.Equal(StoplistMatch.Library, match);
    }

    [Fact]
    public void MatchPrefersGlobalOverLibrary()
    {
        // "the" is in the global stoplist; if also added to per-library
        // stoplist, the response surfaces the global hit (more specific
        // diagnostic for the LLM — they didn't add 'the' themselves).
        var profile = MakeProfile(["the"]);

        var match = Stoplist.Match("the", profile);

        Assert.Equal(StoplistMatch.Global, match);
    }

    [Fact]
    public void ContainsOverloadStillWorksWithoutProfile()
    {
        Assert.True(Stoplist.Contains("the"));
        Assert.False(Stoplist.Contains("MoveLinear"));
    }

    private static LibraryProfile MakeProfile(IReadOnlyList<string> stoplist)
    {
        var result = new LibraryProfile
                         {
                             Id = "test-lib/1.0",
                             LibraryId = "test-lib",
                             Version = "1.0",
                             Source = "test",
                             Stoplist = stoplist
                         };
        return result;
    }
}
