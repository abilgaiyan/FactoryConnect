using System.Security.Cryptography;
using System.Text;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigrationCatalogTests
{
    [Fact]
    public void LoadExistingResourcesReturnsDeterministicLegacyCatalog()
    {
        var catalog = SqlMigrationCatalog.Load();

        Assert.Collection(
            catalog.Migrations,
            migration => AssertMigration(migration, 1, "InitialObservationIngestion", SqlMigrationTransactionPolicy.EngineOwned),
            migration => AssertMigration(migration, 2, "DurableMetricAggregation", SqlMigrationTransactionPolicy.EngineOwned),
            migration => AssertMigration(migration, 3, "BindMetricInputFactMachine", SqlMigrationTransactionPolicy.LegacyMigration003Embedded),
            migration => AssertMigration(migration, 4, "ProductionContextMetricInputHandoff", SqlMigrationTransactionPolicy.EngineOwned));
    }

    [Fact]
    public void LoadEachDescriptorHashesExactCanonicalExecutionBytes()
    {
        var catalog = SqlMigrationCatalog.Load();

        foreach (var migration in catalog.Migrations)
        {
            var bytesFromText = Encoding.UTF8.GetBytes(migration.CanonicalSql);
            Assert.Equal(bytesFromText, migration.CanonicalBytes.ToArray());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytesFromText)), migration.Sha256Checksum);
        }
    }

    [Theory]
    [InlineData("FactoryConnect.Persistence.SqlServer.Sql.01_Bad.sql")]
    [InlineData("FactoryConnect.Persistence.SqlServer.Sql.0001_Bad.sql")]
    [InlineData("FactoryConnect.Persistence.SqlServer.Sql.A01_Bad.sql")]
    [InlineData("FactoryConnect.Persistence.SqlServer.Sql.001_.sql")]
    [InlineData("FactoryConnect.Persistence.SqlServer.Sql.001_Bad.Name.sql")]
    [InlineData("FactoryConnect.Persistence.SqlServer.Other.001_Name.sql")]
    [InlineData("FactoryConnect.Persistence.SqlServer.Sql.001_Name.SQL")]
    public void ParseResourceIdentityInvalidGrammarThrows(string resourceName)
    {
        Assert.Throws<InvalidOperationException>(() => SqlMigrationCatalog.ParseResourceIdentity(resourceName));
    }

    [Fact]
    public void ParseResourceIdentityValidGrammarReturnsIdentity()
    {
        var identity = SqlMigrationCatalog.ParseResourceIdentity(
            "FactoryConnect.Persistence.SqlServer.Sql.017_Add-Reporting_Index.sql");

        Assert.Equal(17, identity.MigrationId);
        Assert.Equal("Add-Reporting_Index", identity.Name);
    }

    [Theory]
    [InlineData("SELECT 1;\r\nSELECT 2;\r\n")]
    [InlineData("SELECT 1;\rSELECT 2;\r")]
    public void CanonicalizeNewlinesNormalizesToLfAndPreservesFinalNewline(string source)
    {
        var canonical = SqlMigrationCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(source));

        Assert.Equal("SELECT 1;\nSELECT 2;\n", canonical.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(canonical.Text), canonical.Bytes.ToArray());
    }

    [Fact]
    public void CanonicalizeNoFinalNewlineDoesNotAddOne()
    {
        var canonical = SqlMigrationCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes("SELECT 1;\r\nSELECT 2;"));

        Assert.Equal("SELECT 1;\nSELECT 2;", canonical.Text);
    }

    [Fact]
    public void CanonicalizeOneLeadingBomRemovesOnlyLeadingBom()
    {
        var payload = Encoding.UTF8.GetBytes("\uFEFFSELECT N'\uFEFF';");
        var source = new byte[payload.Length + 3];
        source[0] = 0xEF;
        source[1] = 0xBB;
        source[2] = 0xBF;
        payload.CopyTo(source, 3);

        var canonical = SqlMigrationCanonicalizer.Canonicalize(source);

        Assert.Equal("\uFEFFSELECT N'\uFEFF';", canonical.Text);
        Assert.Equal(0xEF, canonical.Bytes.Span[0]);
        Assert.Equal(0xBB, canonical.Bytes.Span[1]);
        Assert.Equal(0xBF, canonical.Bytes.Span[2]);
    }

    [Fact]
    public void CanonicalizeInvalidUtf8Throws()
    {
        Assert.Throws<DecoderFallbackException>(() =>
            SqlMigrationCanonicalizer.Canonicalize(new byte[] { 0xC3, 0x28 }));
    }

    [Theory]
    [InlineData("GO")]
    [InlineData("  go")]
    [InlineData("GO 2")]
    [InlineData("GO -- comment")]
    [InlineData("GO;")]
    [InlineData("GO 2;")]
    public void ValidateExecutableGoThrows(string sql)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlMigrationLexicalPolicy.Validate(sql, SqlMigrationTransactionPolicy.EngineOwned));
    }

    [Theory]
    [InlineData("SELECT GO FROM Example;")]
    [InlineData("SELECT [GO];")]
    [InlineData("SELECT 'GO';")]
    [InlineData("SELECT \"GO\";")]
    [InlineData("-- GO")]
    [InlineData("/* GO */")]
    [InlineData("SELECT 'it''s GO';")]
    [InlineData("SELECT \"a\"\"GO\";")]
    [InlineData("SELECT [a]]GO];")]
    [InlineData("/* outer /* GO */ still comment */ SELECT 1;")]
    public void ValidateGoInNonDirectiveContextIsAllowed(string sql)
    {
        SqlMigrationLexicalPolicy.Validate(sql, SqlMigrationTransactionPolicy.EngineOwned);
    }

    [Theory]
    [InlineData("'unterminated")]
    [InlineData("\"unterminated")]
    [InlineData("[unterminated")]
    [InlineData("/* unterminated")]
    public void ValidateUnterminatedLexicalRegionThrows(string sql)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlMigrationLexicalPolicy.Validate(sql, SqlMigrationTransactionPolicy.EngineOwned));
    }

    [Fact]
    public void ValidateLineCommentAtEofIsAllowed()
    {
        SqlMigrationLexicalPolicy.Validate("SELECT 1; -- comment", SqlMigrationTransactionPolicy.EngineOwned);
    }

    [Theory]
    [InlineData("BEGIN TRANSACTION;")]
    [InlineData("begin tran;")]
    [InlineData("BEGIN\nTRANSACTION;")]
    [InlineData("BEGIN /* comment */ TRAN;")]
    [InlineData("COMMIT;")]
    [InlineData("COMMIT TRAN;")]
    [InlineData("COMMIT TRANSACTION;")]
    [InlineData("ROLLBACK;")]
    [InlineData("ROLLBACK TRAN;")]
    [InlineData("ROLLBACK TRANSACTION;")]
    [InlineData("SAVE TRAN savepoint;")]
    [InlineData("SAVE /* comment */ TRANSACTION savepoint;")]
    public void ValidateTransactionControlForEngineOwnedMigrationThrows(string sql)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlMigrationLexicalPolicy.Validate(sql, SqlMigrationTransactionPolicy.EngineOwned));
    }

    [Theory]
    [InlineData("SELECT 'BEGIN TRANSACTION';")]
    [InlineData("SELECT [COMMIT];")]
    [InlineData("SELECT \"ROLLBACK\";")]
    [InlineData("-- SAVE TRAN\nSELECT 1;")]
    [InlineData("/* BEGIN TRAN */ SELECT 1;")]
    public void ValidateTransactionWordsInNonExecutableRegionsAreAllowed(string sql)
    {
        SqlMigrationLexicalPolicy.Validate(sql, SqlMigrationTransactionPolicy.EngineOwned);
    }

    [Fact]
    public void ValidateLegacyMigration003PolicyAllowsEmbeddedTransactionControl()
    {
        SqlMigrationLexicalPolicy.Validate(
            "BEGIN TRANSACTION; UPDATE Example SET Value = 1; COMMIT TRANSACTION;",
            SqlMigrationTransactionPolicy.LegacyMigration003Embedded);
    }

    private static void AssertMigration(
        SqlMigrationDescriptor migration,
        int id,
        string name,
        SqlMigrationTransactionPolicy policy)
    {
        Assert.Equal(id, migration.MigrationId);
        Assert.Equal(name, migration.Name);
        Assert.Equal($"{SqlMigrationCatalog.ResourcePrefix}{id:000}_{name}.sql", migration.ResourceName);
        Assert.Equal(policy, migration.TransactionPolicy);
        Assert.False(string.IsNullOrEmpty(migration.CanonicalSql));
        Assert.NotEmpty(migration.CanonicalBytes.ToArray());
        Assert.Equal(64, migration.Sha256Checksum.Length);
    }
}
