// IdentifierTokenizerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Tests.Symbols;

public sealed class IdentifierTokenizerTests
{
    [Fact]
    public void TokenizesPascalCase()
    {
        var tokens = IdentifierTokenizer.Tokenize("MoveLinear is the canonical motion call.");

        Assert.Contains(tokens, t => t.Name == "MoveLinear");
    }

    [Fact]
    public void TokenizesSnakeCase()
    {
        var tokens = IdentifierTokenizer.Tokenize("Use move_linear() to perform the move.");

        Assert.Contains(tokens, t => t.Name == "move_linear");
    }

    [Fact]
    public void TokenizesScreamingSnake()
    {
        var tokens = IdentifierTokenizer.Tokenize("MAX_VELOCITY is bounded by the controller.");

        Assert.Contains(tokens, t => t.Name == "MAX_VELOCITY");
    }

    [Fact]
    public void TokenizesDottedPath()
    {
        var tokens = IdentifierTokenizer.Tokenize("Set AxisFault.Disabled to clear the latch.");

        var dotted = Assert.Single(tokens, t => t.Name.StartsWith("AxisFault", StringComparison.Ordinal));
        Assert.Equal("AxisFault.Disabled", dotted.Name);
        Assert.Equal("AxisFault", dotted.Container);
        Assert.Equal("Disabled", dotted.LeafName);
    }

    [Fact]
    public void TokenizesDoubleColonPath()
    {
        var tokens = IdentifierTokenizer.Tokenize("Call std::vector::push_back to append.");

        var dotted = Assert.Single(tokens, t => t.LeafName == "push_back");
        Assert.Equal("std::vector::push_back", dotted.Name);
        Assert.Equal("std::vector", dotted.Container);
    }

    [Fact]
    public void StripsTrailingPeriodFromProseMention()
    {
        // The bug being fixed: "AxisFault." in prose used to leak with the period attached.
        var tokens = IdentifierTokenizer.Tokenize("See the AxisFault. enumeration for details.");

        Assert.Contains(tokens, t => t.Name == "AxisFault");
        Assert.DoesNotContain(tokens, t => t.Name.EndsWith(".", StringComparison.Ordinal));
    }

    [Fact]
    public void RecognizesCallableShape()
    {
        var tokens = IdentifierTokenizer.Tokenize("Invoke MoveLinear(axis, distance).");

        var callable = Assert.Single(tokens, t => t.Name == "MoveLinear");
        Assert.True(callable.HasCallableShape);
    }

    [Fact]
    public void RecognizesGenericShape()
    {
        var tokens = IdentifierTokenizer.Tokenize("Pass List<int> to the helper.");

        var generic = Assert.Single(tokens, t => t.Name == "List");
        Assert.True(generic.HasGenericShape);
    }

    [Fact]
    public void RecognizesDeclaredFormFromKeyword()
    {
        var tokens = IdentifierTokenizer.Tokenize("class Controller : IDisposable { }");

        var declared = Assert.Single(tokens, t => t.Name == "Controller");
        Assert.True(declared.IsDeclared);
        Assert.Equal("class", declared.DeclaredFormKeyword);
    }

    [Fact]
    public void RecognizesEnumDeclarationKeyword()
    {
        var tokens = IdentifierTokenizer.Tokenize("enum HomeType { ToLimit, ToMarker }");

        var declared = Assert.Single(tokens, t => t.Name == "HomeType");
        Assert.True(declared.IsDeclared);
        Assert.Equal("enum", declared.DeclaredFormKeyword);
    }

    [Fact]
    public void HandlesEmptyInput()
    {
        var tokens = IdentifierTokenizer.Tokenize(string.Empty);

        Assert.Empty(tokens);
    }

    [Fact]
    public void DedupsRepeatedIdenticalMentions()
    {
        var tokens = IdentifierTokenizer.Tokenize("MoveLinear MoveLinear MoveLinear");

        Assert.Single(tokens, t => t.Name == "MoveLinear");
    }
}
