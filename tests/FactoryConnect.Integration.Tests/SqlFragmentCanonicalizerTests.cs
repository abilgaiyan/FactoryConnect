using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlFragmentCanonicalizerTests
{
    [Theory]
    [InlineData("A > 0", "A > 0")]
    [InlineData("  A\r\n>\r0  ", "A > 0")]
    [InlineData("A/* comment */>-- line\n0", "A > 0")]
    [InlineData("A/* outer /* nested */ comment */>0", "A > 0")]
    public void CanonicalizeRemovesOnlyInsignificantSeparation(string input, string expected)
    {
        Assert.Equal(expected, SqlFragmentCanonicalizer.Canonicalize(input));
    }

    [Theory]
    [InlineData("CHECK (A > 0 AND B > 0)", "CHECK ( A > 0 AND B > 0 )")]
    [InlineData("CHECK ((A > 0))", "CHECK ( ( A > 0 ) )")]
    [InlineData("[A]]B] >= 10", "[A]]B] >= 10")]
    [InlineData("\"A\"\"B\" <> 'it''s'", "\"A\"\"B\" <> 'it''s'")]
    public void CanonicalizePreservesTokenTextAndParentheses(string input, string expected)
    {
        Assert.Equal(expected, SqlFragmentCanonicalizer.Canonicalize(input));
    }

    [Fact]
    public void CanonicalizeDoesNotReorderEquivalentBooleanExpressions()
    {
        var left = SqlFragmentCanonicalizer.Canonicalize("CHECK (A > 0 AND B > 0)");
        var right = SqlFragmentCanonicalizer.Canonicalize("CHECK (B > 0 AND A > 0)");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void CanonicalizeDoesNotRemoveParentheses()
    {
        var nested = SqlFragmentCanonicalizer.Canonicalize("CHECK ((A > 0))");
        var single = SqlFragmentCanonicalizer.Canonicalize("CHECK (A > 0)");

        Assert.NotEqual(nested, single);
    }

    [Theory]
    [InlineData("A > 'unterminated")]
    [InlineData("A > \"unterminated")]
    [InlineData("[unterminated")]
    [InlineData("A > 0 /* unterminated")]
    public void CanonicalizeRejectsUnterminatedLexicalRegions(string input)
    {
        Assert.Throws<InvalidOperationException>(() => SqlFragmentCanonicalizer.Canonicalize(input));
    }

    [Fact]
    public void CanonicalizePreservesIdentifierCaseAndLiteralSpelling()
    {
        var first = SqlFragmentCanonicalizer.Canonicalize("MetricValue = 01");
        var second = SqlFragmentCanonicalizer.Canonicalize("metricvalue = 1");

        Assert.NotEqual(first, second);
    }
}
