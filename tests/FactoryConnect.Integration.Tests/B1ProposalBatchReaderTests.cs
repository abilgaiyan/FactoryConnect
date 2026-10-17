namespace FactoryConnect.Integration.Tests;

// Ordinary tests: exercise Windows/Unix checkout text without a SQL connection.
public sealed class B1ProposalBatchReaderTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void CheckoutLineEndingsPreserveAllTwelveBatchesAndTriggerBoundaries(string newline)
    {
        var original = ReadProposal();
        var expected = B1ProposalBatchReader.Split(original);
        var actual = B1ProposalBatchReader.Split(original.ReplaceLineEndings(newline));
        Assert.Equal(12, actual.Length);
        Assert.Equal(expected, actual);
        var triggers = actual.Where(batch => batch.Contains("CREATE TRIGGER", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, triggers.Length);
        Assert.All(triggers, batch => Assert.StartsWith("CREATE TRIGGER", batch.TrimStart(), StringComparison.Ordinal));
    }

    [Fact]
    public void LostDelimitersFailBeforeSubmittingCombinedDdlToSql()
    {
        var corrupted = ReadProposal().Replace("-- B1 VALIDATION BATCH --", "-- lost delimiter --", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => B1ProposalBatchReader.Split(corrupted));
    }

    private static string ReadProposal()
    {
        using var stream = typeof(B1ProposalBatchReaderTests).Assembly.GetManifestResourceStream("B1RC2.ProposedDdl")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
