// QueryShapeClassifierTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Embedding;

public sealed class QueryShapeClassifierTests
{
    [Theory]
    [InlineData("MoveLinear")]
    [InlineData("AxisStatus")]
    [InlineData("Controller.Connect")]
    [InlineData("AxisFault.Disabled")]
    [InlineData("MoveLinear()")]
    [InlineData("std::vector")]
    [InlineData("move_linear")]
    [InlineData("HomeType.ToLimit")]
    public void RecognizesIdentifierShapedQueries(string query)
    {
        Assert.True(QueryShapeClassifier.IsIdentifierShaped(query));
    }

    [Theory]
    [InlineData("how do I configure homing")]
    [InlineData("explain coordinated motion")]
    [InlineData("set up encoder feedback")]
    [InlineData("what is the best way to handle errors")]
    [InlineData("introduction to the library")]
    public void RecognizesProseShapedQueries(string query)
    {
        Assert.False(QueryShapeClassifier.IsIdentifierShaped(query));
    }

    [Fact]
    public void MixedQueryWithIdentifierIsTreatedAsIdentifier()
    {
        // A prose phrase combined with a CamelCase identifier should classify as identifier-shaped
        // because the identifier signal is the dominant routing concern.
        Assert.True(QueryShapeClassifier.IsIdentifierShaped("how do I use MoveLinear in my code"));
    }
}
