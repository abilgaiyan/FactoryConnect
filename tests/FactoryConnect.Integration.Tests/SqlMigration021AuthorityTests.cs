using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigration021AuthorityTests
{
    [Fact]
    public void HistoricalChecksumsThrough020RemainFrozen()
    {
        string[] expected =
        [
            "E1C14282B7A246BBD9D5734370498695721D3F0A78D60F74531E35D5FEDC9057",
            "F8DA0AFF348E3ED8964D5ED03042581A55D7C94898AAC739B81E60CA7F5E5113",
            "98A9635782C4D822441269ECEE8E13BBCDC5A61C07B64608F81A0107133535C6",
            "786CDD68F66E222A4E4EFB8220595E46390A0F81880D0D45A54FA22DD7A498D5",
            "53F337BB5B3294D331AB6E363C7301A88D0A4CD090762151CFD1508405DEDFC7",
            "DDFD8C6CC1FADE9D7486EB5D3D915881A2538E2724A6B57EDA7E6D7C16EB6EEE",
            "5844FDD5B3866755BB5F2A36738DCFC13064D358F0C16D4CCAC3FE5B95EC00FD",
            "0DDCF07F7CD492F0981B06C3457581E1F599901A87DF420136C2FF1791F1B9E1",
            "9351AE4BC31FC38F416537EA50498F2BC5AA38BD8AFF102702C60627EE32021E",
            "9CCE200D47A2FC379D105477A1462153F0392A74AA81775AEC6A56881574DD00",
            "2A65F014DABD3176466C61FFE08FB2261BE4944B7C7DD2E16A673F934A89E7B6",
            "347635BCE1E2406EFC34D1583455FBE827331A19B9FE66B0031BFE70ECF01915",
            "17B12EF16A2D60BE512663DFA777DB0FFF773CF88E3AE1E39D6670FC9BA8816C",
            "F2FC5C4E3DAA9A9A3E4ED01FF042EFECC89B7010E2C3DE0CDD229F1B4B12F782",
            "6D0C84128EE7652C4E175C79EA253623418416085D766827770A7ACBC98CE464",
            "16224E51F57CB0B3BE60A9BC3B6A5CCAA92D38CE0606328579AED528F926401E",
            "C7A83608E44FC02991FA1A6E066FF07F9466A29888B3D0EC79B32DF45CD47785",
            "42F0AEACAC578270A7477A0066BDD6C355852305A9259FE6B92931DB054F7DE5",
            "22257AF80814D93EE13159DA41A34D9E6C72D32A5FAA81887CD12E90EBD3CDCC",
            "0F01F8614293EE7EB5EECAB6CEC48C6EEB505C0751437AAC025B0216769C07AD"
        ];
        Assert.Equal(expected, SqlMigrationCatalog.Load().Migrations.Take(20).Select(m => m.Sha256Checksum));
    }

    [Fact]
    public void ProductionResourceIsTheReviewedDdlWithOnlyExecutionMarkerRenamed()
    {
        var migration = SqlMigrationCatalog.Load().Migrations[20];
        using var resource = typeof(SqlMigration021AuthorityTests).Assembly.GetManifestResourceStream("B1RC2.ProposedDdl")!;
        using var reader = new StreamReader(resource);
        var expected = reader.ReadToEnd().ReplaceLineEndings("\n").Replace("-- B1 VALIDATION BATCH --", SqlMigrationExecutionBatches.Marker, StringComparison.Ordinal);
        Assert.Equal(expected, migration.CanonicalSql);
        var batches = SqlMigrationExecutionBatches.For(migration);
        Assert.Equal(12, batches.Length);
        Assert.Equal(expected, string.Join("\n" + SqlMigrationExecutionBatches.Marker + "\n", batches));
        foreach (var old in SqlMigrationCatalog.Load().Migrations.Take(20)) Assert.Equal(new[] { old.CanonicalSql }, SqlMigrationExecutionBatches.For(old));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ExecutionPlanPreservesCheckoutLineEndingsAndRejectsMissingBoundaries(string newline)
    {
        var migration = SqlMigrationCatalog.Load().Migrations[20];
        Assert.Equal(SqlMigrationExecutionBatches.For(migration), SqlMigrationExecutionBatches.For(migration with { CanonicalSql = migration.CanonicalSql.ReplaceLineEndings(newline) }));
        Assert.Throws<InvalidDataException>(() => SqlMigrationExecutionBatches.For(migration with { CanonicalSql = migration.CanonicalSql.Replace(SqlMigrationExecutionBatches.Marker, "-- lost --", StringComparison.Ordinal) }));
        Assert.Throws<InvalidDataException>(() => SqlMigrationExecutionBatches.For(migration with { Name = "Other" }));
        Assert.Throws<InvalidDataException>(() => SqlMigrationExecutionBatches.For(migration with { MigrationId = 22 }));
    }

    [Fact]
    public void DescriptorAddsOnlyReviewedTablesColumnsAndConstraints()
    {
        var previous = SqlRepositorySchemaDescriptors.Post020; var current = SqlRepositorySchemaDescriptors.Post021;
        Assert.Equal(48, previous.Tables.Length); Assert.Equal(51, current.Tables.Length);
        foreach (var table in previous.Tables.Where(t => t.Name.ObjectName is not ("OperationalMetricProjectionProcessor" or "OperationalMetricProjection" or "OperationalMetricProjectionEvidence")))
            Assert.Same(table, current.Tables.Single(t => t.Name == table.Name));
        Assert.Equal(3, current.Tables.Sum(t => t.Triggers.IsDefault ? 0 : t.Triggers.Length));
        Assert.Equal(2, current.Tables.Sum(t => t.DefaultConstraints.IsDefault ? 0 : t.DefaultConstraints.Length));
        foreach (var table in current.Tables.Skip(48))
            Assert.All(table.ForeignKeys, fk => { Assert.Equal(SqlReferentialAction.NoAction, fk.DeleteAction); Assert.Equal(SqlReferentialAction.NoAction, fk.UpdateAction); });
    }

    [Fact]
    public void AuxiliaryAuthorityDetectsDisabledOrReplacedTriggersAndChangedDefaults()
    {
        var current = SqlRepositorySchemaDescriptors.Current;
        var archive = current.Tables.Single(t => t.Name.ObjectName == "OperationalMetricConflictWitnessArchive");
        var changed = current with { Tables = [.. current.Tables.Select(t => t == archive ? t with { Triggers = [archive.Triggers[0] with { IsEnabled = false }] } : t)] };
        Assert.Contains(SqlSchemaComparator.Compare(current, changed).Differences, d => d.Kind == SqlSchemaDifferenceKind.TriggerMismatch);
        var processor = current.Tables.Single(t => t.Name.ObjectName == "OperationalMetricProjectionProcessor");
        changed = current with { Tables = [.. current.Tables.Select(t => t == processor ? t with { DefaultConstraints = [new("DF_OMPP_PublicationFormat", "PublicationFormatVersion", "(2)")] } : t)] };
        Assert.Contains(SqlSchemaComparator.Compare(current, changed).Differences, d => d.Kind == SqlSchemaDifferenceKind.DefaultConstraintMismatch);
    }
}
