using System.Globalization;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class ContainmentCodecVectorTests
{
    public static IEnumerable<object[]> VectorIds() => Vectors().Select(v => new object[] { v.GetProperty("id").GetString()! });
    public static IEnumerable<object[]> NegativeIds() => Package().GetProperty("negativeCandidates").EnumerateArray()
        .Select(v => new object[] { v.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(VectorIds))]
    public void IndependentModelEncodingMatchesAcceptedBytesAndAllDigestDomains(string id)
    {
        var v = Vector(id); var expected = Convert.FromHexString(Text(v, "hex")); var model = v.GetProperty("model");
        var metadata = v.GetProperty("metadata"); byte[] encoded;
        if (Text(v, "kind") == "witness")
        {
            // Construct from explicit vector inputs, without decoding expected bytes.
            encoded = OperationalMetricConflictWitnessV1Codec.Encode(Witness(model, metadata));
            Assert.Equal(Text(v.GetProperty("digests"), "witness"),
                Convert.ToHexStringLower(OperationalMetricContainmentIdentity.ComputeWitnessId(encoded)));
            Assert.Equal(encoded, OperationalMetricConflictWitnessV1Codec.Encode(OperationalMetricConflictWitnessV1Codec.Decode(expected)));
        }
        else
        {
            encoded = OperationalMetricDependencySnapshotV2Codec.Encode(Result(model, metadata));
            Assert.Equal(Text(v.GetProperty("digests"), "result"),
                Convert.ToHexStringLower(OperationalMetricContainmentIdentity.ComputeResultId(encoded)));
            Assert.Equal(Text(v.GetProperty("digests"), "dependency"),
                Convert.ToHexStringLower(OperationalMetricContainmentIdentity.ComputeDependencyHash(encoded)));
            Assert.Equal(encoded, OperationalMetricDependencySnapshotV2Codec.Encode(OperationalMetricDependencySnapshotV2Codec.Decode(expected)));
        }
        Assert.Equal(v.GetProperty("byteLength").GetInt32(), encoded.Length);
        Assert.Equal(expected, encoded);
    }

    [Theory]
    [MemberData(nameof(NegativeIds))]
    public void AcceptedNegativeCandidatesFailWithoutVersionFallback(string id)
    {
        var v = Package().GetProperty("negativeCandidates").EnumerateArray().Single(v => Text(v, "id") == id);
        var bytes = Convert.FromHexString(Text(v, "hex"));
        Assert.Equal(Text(v, "recomputedResultDigest"), Convert.ToHexStringLower(OperationalMetricContainmentIdentity.ComputeResultId(bytes)));
        if (id == "unsupported-version") Assert.Throws<UnsupportedContainmentCodecException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(bytes));
        else Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(bytes));
    }

    [Fact]
    public void NestedWitnessAndCompletedEmptyAuthorityRemainDistinctFromRootWitnessAndMembershipProof()
    {
        var d = OperationalMetricDependencySnapshotV2Codec.Decode(Convert.FromHexString(Text(Vector("oee-nested-unavailable-availability"), "hex")));
        Assert.Null(d.Witness);
        var availability = d.Dependencies[0].Result;
        Assert.Equal(380UL, availability.Witness!.Contribution.Position.Value);
        Assert.Equal(534UL, availability.SourceRevision.Position.Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, availability.ReasonCode);
        var performance = d.Dependencies[1].Result;
        Assert.Equal(0, performance.Authority!.CompletedReferenceRevision);
        Assert.Empty(performance.Authority.SourceQuantityIds);
        Assert.Empty(performance.Authority.StandardRevisions);
        Assert.IsType<ContainmentWitnessDocument>(availability.Witness);
        // No decoder returns OperationalMetricProjection or constructs the internal
        // AvailabilityScopeConflictEvidenceV1 membership proof.
    }

    [Fact]
    public void WitnessTamperingAndForeignBindingCannotBeEncodedEvenWithRecomputedDigests()
    {
        var v = Vector("gg003-380-shift-witness"); var w = Witness(v.GetProperty("model"), v.GetProperty("metadata"));
        var fact = w.Contribution.Fact;
        foreach (var changed in new[] {
            fact with { IsPlannedProductionTime = true }, fact with { SourceEligibilityIntervalId = null },
            fact with { Value = fact.Value + 1 }, fact with { PlannedProductionScheduleAssignmentId = new("foreign") }
        })
        {
            var forged = w with { Contribution = new(w.Contribution.StreamId, w.Contribution.Position, changed, w.Contribution.ShiftOccurrenceId, w.Contribution.ProductionDayId) };
            Assert.Throws<InvalidDataException>(() => OperationalMetricConflictWitnessV1Codec.Encode(forged));
        }
        var late = w with { Contribution = new(w.Contribution.StreamId, new(535), fact, w.Contribution.ShiftOccurrenceId, w.Contribution.ProductionDayId) };
        Assert.Throws<InvalidDataException>(() => OperationalMetricConflictWitnessV1Codec.Encode(late));
        var badName = w with { Numerator = ComponentWithName(w.Numerator, "Foreign") };
        Assert.Throws<InvalidDataException>(() => OperationalMetricConflictWitnessV1Codec.Encode(badName));
    }

    [Fact]
    public void ComponentsDependenciesAuthorityAndCalculatedValueHaveStrictSemantics()
    {
        var v = Vector("availability-75-no-witness"); var d = Result(v.GetProperty("model"), v.GetProperty("metadata"));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(d, components: d.Components.Reverse())));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(d, components: [d.Components[0], d.Components[0]])));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(d, value: 0.74m)));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(d, authority: new(0, [], []))));
        v = Vector("oee-nested-unavailable-availability"); d = Result(v.GetProperty("model"), v.GetProperty("metadata"));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(d, dependencies: d.Dependencies.Reverse())));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(d, dependencies: d.Dependencies.Take(2))));
        Assert.Throws<InvalidDataException>(() => _ = new ContainmentHistoricalAuthority(0, ["b", "a"], []));
        Assert.Throws<InvalidDataException>(() => _ = new ContainmentHistoricalAuthority(0, ["a", "a"], []));
        Assert.Throws<InvalidDataException>(() => _ = new ContainmentHistoricalAuthority(0, [], [2, 1]));
        Assert.Throws<InvalidDataException>(() => _ = new ContainmentHistoricalAuthority(0, [], [1, 1]));
        Assert.Throws<InvalidDataException>(() => _ = new ContainmentHistoricalAuthority(0, [], [-1]));
    }

    [Fact]
    public void PrimitiveCanonicalityAndBoundsAreEnforcedBeforeAllocation()
    {
        foreach (var p in Package().GetProperty("primitives").EnumerateArray())
        {
            using var w = new ContainmentBinaryWriter(1024); w.Decimal(decimal.Parse(Text(p, "decimalInput"), NumberStyles.Float, CultureInfo.InvariantCulture));
            Assert.Equal(Text(p, "hex"), Convert.ToHexStringLower(w.ToArray()));
            var r = new ContainmentBinaryReader(w.ToArray()); Assert.Equal(Text(p, "canonical"), CanonicalDecimalTextV1Codec.Serialize(r.Decimal())); r.End();
        }
        using var unicode = new ContainmentBinaryWriter(1024); unicode.String("Aé😀");
        Assert.Equal("0000000741c3a9f09f9880", Convert.ToHexStringLower(unicode.ToArray()));
        Assert.Equal("Aé😀", new ContainmentBinaryReader(unicode.ToArray()).String());
        Assert.Throws<System.Text.EncoderFallbackException>(() => unicode.String("\uD800"));
        Assert.Throws<System.Text.DecoderFallbackException>(() => new ContainmentBinaryReader(Convert.FromHexString("00000002C328")).String());
        Assert.Throws<InvalidDataException>(() => new ContainmentBinaryReader(Convert.FromHexString("FFFFFFFF")).Blob(768));
        Assert.Throws<InvalidDataException>(() => new ContainmentBinaryReader(Convert.FromHexString("FFFFFFFF")).Count(65_535, 1));
        Assert.Throws<InvalidDataException>(() => new ContainmentBinaryReader(new byte[] { 2 }).Present());
        Assert.Throws<InvalidDataException>(() => unicode.Timestamp(new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.FromHours(1))));
        Assert.Throws<InvalidDataException>(() => unicode.String(new string('x', 257)));
    }

    [Fact]
    public void ProductionDayAndNonemptyPerformanceAuthorityRoundTripWithoutClaimingHistory()
    {
        var v = Vector("gg003-380-shift-witness"); var shift = Witness(v.GetProperty("model"), v.GetProperty("metadata"));
        var period = new OperationalMetricPeriodId.ProductionDay(shift.Contribution.ProductionDayId);
        OperationalMetricComponentProjectionEvidence ForDay(OperationalMetricComponentProjectionEvidence c) => new(c.OperandName,
            new(c.SourceIdentity.ProcessorId, c.SourceIdentity.MachineId, period, c.SourceIdentity.ComponentKey), c.SourceRevision,
            c.Dimension, c.Value, c.Unit, c.InputCount, c.FirstInputTimestamp, c.LastInputTimestamp);
        var day = shift with { Period = period, Numerator = ForDay(shift.Numerator), Denominator = ForDay(shift.Denominator) };
        var bytes = OperationalMetricConflictWitnessV1Codec.Encode(day);
        Assert.Equal(bytes, OperationalMetricConflictWitnessV1Codec.Encode(OperationalMetricConflictWitnessV1Codec.Decode(bytes)));
        var result = new ContainmentResultDocument(AvailabilityContainmentV1.ProjectionProcessorId(shift.SourceRevision.StreamId.MachineId),
            new(shift.SourceRevision.StreamId.MachineId, period, new("availability", "1.0"), new()), OperationalMetricEvaluationStatus.Unavailable,
            null, OperationalMetricUnits.Ratio, OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction,
            "ActualProductionTime", shift.SourceRevision, null, day, [day.Numerator, day.Denominator], []);
        bytes = OperationalMetricDependencySnapshotV2Codec.Encode(result);
        Assert.Equal(bytes, OperationalMetricDependencySnapshotV2Codec.Encode(OperationalMetricDependencySnapshotV2Codec.Decode(bytes)));

        v = Vector("oee-nested-unavailable-availability"); result = Result(v.GetProperty("model"), v.GetProperty("metadata")).Dependencies[1].Result;
        // UTF-16 ordinal order places the supplementary character before U+E000.
        var authority = new ContainmentHistoricalAuthority(long.MaxValue, ["a", "😀", "\uE000"], [0, 1, long.MaxValue]);
        bytes = OperationalMetricDependencySnapshotV2Codec.Encode(Copy(result, authority: authority));
        var decoded = OperationalMetricDependencySnapshotV2Codec.Decode(bytes);
        Assert.Equal(authority.SourceQuantityIds, decoded.Authority!.SourceQuantityIds);
        Assert.Equal(authority.StandardRevisions, decoded.Authority.StandardRevisions);
        Assert.Equal(long.MaxValue, decoded.Authority.CompletedReferenceRevision);
    }

    [Fact]
    public void EveryTruncatedWitnessAndResultFailsAndUnknownWitnessVersionIsExplicit()
    {
        var witness = Convert.FromHexString(Text(Vector("gg003-380-shift-witness"), "hex"));
        for (var length = 0; length < witness.Length; length++)
        {
            var prefix = witness[..length];
            Assert.Throws<InvalidDataException>(() => OperationalMetricConflictWitnessV1Codec.Decode(prefix));
        }
        var result = Convert.FromHexString(Text(Vector("availability-gg003-unavailable"), "hex"));
        for (var length = 0; length < result.Length; length++)
        {
            var prefix = result[..length];
            Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(prefix));
        }
        witness[6] = 2;
        Assert.Throws<UnsupportedContainmentCodecException>(() => OperationalMetricConflictWitnessV1Codec.Decode(witness));
        Assert.Throws<InvalidDataException>(() => OperationalMetricConflictWitnessV1Codec.Decode(new byte[65_537]));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(new byte[16_777_217]));
    }

    [Fact]
    public void ForgedCountsNestedDepthAndNoncanonicalPrimitiveValuesFailBeforeInterpretation()
    {
        var v = Vector("availability-75-no-witness"); var d = Result(v.GetProperty("model"), v.GetProperty("metadata"));
        using (var w = Prefix(d))
        {
            w.U32(uint.MaxValue);
            Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(w.ToArray()));
        }
        var bytes = Convert.FromHexString(Text(v, "hex"));
        for (var i = 0; i < 64; i++)
        {
            using var w = Prefix(d); w.U32(0); w.U32(1); w.String("Availability"); w.String("availability"); w.String("1.0");
            w.Blob(bytes, OperationalMetricContainmentIdentity.MaximumResultLength); bytes = w.ToArray();
        }
        var error = Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(bytes));
        Assert.Contains("depth", error.Message, StringComparison.Ordinal);
        foreach (var text in new[] { "-0", "0.750", " 1", "1E+00" })
        {
            using var w = new ContainmentBinaryWriter(100); w.String(text);
            Assert.Throws<InvalidDataException>(() => new ContainmentBinaryReader(w.ToArray()).Decimal());
        }
        using var ticks = new ContainmentBinaryWriter(100); ticks.I64(-1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainmentBinaryReader(ticks.ToArray()).Timestamp());
        using var guid = new ContainmentBinaryWriter(100); guid.Guid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        Assert.Equal("00112233445566778899aabbccddeeff", Convert.ToHexStringLower(guid.ToArray()));
    }

    [Fact]
    public void RealStageAWitnessEncodesWithoutRelaxingTheExistingPublicationOrV1Guards()
    {
        var v = Vector("minimal-witness"); var w = Witness(v.GetProperty("model"), v.GetProperty("metadata"));
        var running = w.Contribution;
        var planned = new PositionedMetricInputFact(running.StreamId, new(2), running.Fact with
        {
            Id = new("minimal-planned"), Key = MetricInputFactKeys.PlannedProductionDuration,
            StartsAtUtc = running.Fact.EndsAtUtc, EndsAtUtc = running.Fact.EndsAtUtc.AddSeconds(1),
            IsPlannedProductionTime = true, PlannedProductionScheduleAssignmentId = new("minimal-plan"),
            SourceEligibilityIntervalId = new("minimal-planned-eligibility"), SourceContextualizedActivityIntervalId = new("minimal-planned-activity")
        }, running.ShiftOccurrenceId, running.ProductionDayId);
        OperationalMetricComponent ComponentForBuilder(OperationalMetricComponentProjectionEvidence c) => new(c.OperandName, c.SourceIdentity,
            c.Dimension, new(c.Value, c.Unit, c.InputCount, c.FirstInputTimestamp, c.LastInputTimestamp));
        var key = new OperationalMetricEvaluationKey(running.StreamId.MachineId, w.Period, BuiltInOperationalMetricDefinitions.AvailabilityId, new());
        var definition = BuiltInOperationalMetricDefinitions.All.Single(d => d.Id == key.DefinitionId);
        var proof = AvailabilityScopeConflictEvidenceBuilder.Build(new(key, w.SourceRevision.ProcessorId, definition.Operands, true),
            w.SourceRevision, [running, planned], [ComponentForBuilder(w.Numerator), ComponentForBuilder(w.Denominator)])!;
        Assert.NotNull(proof);
        Assert.Equal(Convert.FromHexString(Text(v, "hex")), OperationalMetricConflictWitnessV1Codec.Encode(ContainmentWitnessDocument.FromEvidence(proof)));
        var processor = AvailabilityContainmentV1.ProjectionProcessorId(key.MachineId);
        var projection = new OperationalMetricProjection(processor, key, OperationalMetricEvaluationStatus.Unavailable, null,
            OperationalMetricUnits.Ratio, OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, "ActualProductionTime",
            w.SourceRevision, [w.Numerator, w.Denominator], availabilityScopeConflictEvidence: proof, evaluationPolicyId: AvailabilityContainmentV1.PolicyId);
        var document = ContainmentResultDocument.FromProjection(projection, static _ => null);
        var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(document);
        Assert.Equal(bytes, OperationalMetricDependencySnapshotV2Codec.Encode(OperationalMetricDependencySnapshotV2Codec.Decode(bytes)));
        Assert.Throws<NotSupportedException>(() => OperationalMetricDependencySnapshotV1Codec.Encode(projection));
        Assert.Throws<NotSupportedException>(() => _ = new OperationalMetricProjectionCommit(processor, null, new(processor, w.SourceRevision), [projection]));
    }

    [Fact]
    public void FrozenV1NestedBytesAndUndomainedHashRemainAtTheOriginalBaseline()
    {
        var v = Vector("availability-75-no-witness"); var d = Result(v.GetProperty("model"), v.GetProperty("metadata"));
        var processor = new OperationalMetricProjectionProcessorId("legacy-v1");
        var availability = new OperationalMetricProjection(processor, d.Key, d.Status, d.Value, d.Unit, null, null, d.SourceRevision, d.Components);
        var oee = new OperationalMetricProjection(processor, new(d.Key.MachineId, d.Key.PeriodId, BuiltInOperationalMetricDefinitions.OeeId, new()),
            d.Status, d.Value, d.Unit, null, null, d.SourceRevision,
            dependencyEvidence: [new("Availability", availability.Key.DefinitionId, availability)]);
        var golden = Package().GetProperty("legacyV1"); var bytes = OperationalMetricDependencySnapshotV1Codec.Encode(oee);
        Assert.Equal(Convert.FromHexString(Text(golden, "hex")), bytes);
        Assert.Equal(golden.GetProperty("byteLength").GetInt32(), bytes.Length);
        Assert.Equal(Text(golden, "sha256"), Convert.ToHexStringLower(OperationalMetricDependencySnapshotV1Codec.ComputeHash(bytes)));
        Assert.Equal(bytes, OperationalMetricDependencySnapshotV1Codec.Encode(OperationalMetricDependencySnapshotV1Decoder.Decode(bytes)));
        Assert.Throws<UnsupportedContainmentCodecException>(() => OperationalMetricDependencySnapshotV2Codec.Decode(bytes));
        Assert.Throws<InvalidDataException>(() => ContainmentResultDocument.FromProjection(availability, static _ => null));
    }

    [Fact]
    public void CalculatedOeeUsesUnroundedRatiosAndRejectsRoundedFactorForgery()
    {
        var v = Vector("availability-75-no-witness"); var template = Result(v.GetProperty("model"), v.GetProperty("metadata"));
        ContainmentResultDocument Ratio(OperationalMetricDefinitionId id, decimal numerator, decimal denominator)
        {
            var definition = BuiltInOperationalMetricDefinitions.All.Single(d => d.Id == id);
            var components = definition.Operands.Select((o, i) => new OperationalMetricComponentProjectionEvidence(o.OperandName,
                new(template.SourceRevision.ProcessorId, template.Key.MachineId, template.Key.PeriodId,
                    OperationalMetricComponentKeyMapping.AggregateKey(((OperationalMetricOperandSource.Component)o.Source).ComponentKey)),
                template.SourceRevision, o.RequiredDimension, i == 0 ? numerator : denominator, o.RequiredUnit, 1,
                template.Components[0].FirstInputTimestamp, template.Components[0].LastInputTimestamp));
            return new(template.ProcessorId, new(template.Key.MachineId, template.Key.PeriodId, id, new()),
                OperationalMetricEvaluationStatus.Calculated, decimal.Round(numerator / denominator, 8, MidpointRounding.ToEven),
                template.Unit, null, null, template.SourceRevision, id == BuiltInOperationalMetricDefinitions.PerformanceId ? new(0, [], []) : null,
                null, components, []);
        }
        var a = Ratio(BuiltInOperationalMetricDefinitions.AvailabilityId, 1, 2);
        var p = Ratio(BuiltInOperationalMetricDefinitions.PerformanceId, 6, 11);
        var q = Ratio(BuiltInOperationalMetricDefinitions.QualityId, 5, 7);
        var oee = new ContainmentResultDocument(template.ProcessorId, new(template.Key.MachineId, template.Key.PeriodId, BuiltInOperationalMetricDefinitions.OeeId, new()),
            OperationalMetricEvaluationStatus.Calculated, 0.19480519m, template.Unit, null, null, template.SourceRevision, null, null, [],
            [new("Availability", a.Key.DefinitionId, a), new("Performance", p.Key.DefinitionId, p), new("Quality", q.Key.DefinitionId, q)]);
        var bytes = OperationalMetricDependencySnapshotV2Codec.Encode(oee);
        Assert.Equal(bytes, OperationalMetricDependencySnapshotV2Codec.Encode(OperationalMetricDependencySnapshotV2Codec.Decode(bytes)));
        Assert.Throws<InvalidDataException>(() => OperationalMetricDependencySnapshotV2Codec.Encode(Copy(oee, value: 0.19480520m)));
    }

    private static ContainmentBinaryWriter Prefix(ContainmentResultDocument d)
    {
        var w = new ContainmentBinaryWriter(OperationalMetricContainmentIdentity.MaximumResultLength);
        w.Bytes("FCDS"u8); w.U8(2); w.U16(2); w.String(d.ProcessorId.Value);
        w.Blob(OperationalMetricEvaluationKeyV1Codec.Encode(d.Key), 6992); w.String(AvailabilityContainmentV1.PolicyId);
        w.U8((byte)d.Status); w.OptionalDecimal(d.Value); w.String(d.Unit); w.U8(0); w.OptionalString(null);
        ContainmentStructures.WriteCut(w, d.SourceRevision); w.U8(0); w.U8(0); return w;
    }

    private static ContainmentResultDocument Copy(ContainmentResultDocument d,
        IEnumerable<OperationalMetricComponentProjectionEvidence>? components = null, IEnumerable<ContainmentDependencyDocument>? dependencies = null,
        decimal? value = null, ContainmentHistoricalAuthority? authority = null) => new(d.ProcessorId, d.Key, d.Status, value ?? d.Value, d.Unit,
            d.ReasonCode, d.ReasonOperandName, d.SourceRevision, authority ?? d.Authority, d.Witness, components ?? d.Components, dependencies ?? d.Dependencies);

    private static OperationalMetricComponentProjectionEvidence ComponentWithName(OperationalMetricComponentProjectionEvidence c, string name) =>
        new(name, c.SourceIdentity, c.SourceRevision, c.Dimension, c.Value, c.Unit, c.InputCount, c.FirstInputTimestamp, c.LastInputTimestamp);

    private static JsonElement Package()
    {
        using var stream = typeof(ContainmentCodecVectorTests).Assembly.GetManifestResourceStream("ContainmentCodec.RC2Vectors")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    private static JsonElement[] Vectors() => Package().GetProperty("vectors").EnumerateArray().ToArray();
    private static JsonElement Vector(string id) => Vectors().Single(v => Text(v, "id") == id);
    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static string? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;
    private static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static decimal Number(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static MetricAggregationCheckpoint Cut(JsonElement metadata) => new(new(Text(metadata, "aggregationProcessor")),
        MetricInputStreamId.ForMachine(new(Guid.Parse(Text(metadata, "machine")))), new(metadata.GetProperty("revision").GetUInt64()));
    private static OperationalMetricPeriodId.Shift Period(JsonElement m) => new OperationalMetricPeriodId.Shift(new(new("rca-site"),
        new("rca-shift-1-schedule"), new("SHIFT-1"), Time(Text(m, "shiftStart")), Time(Text(m, "shiftEnd"))));

    private static OperationalMetricComponentProjectionEvidence Component(JsonElement e, JsonElement m) => new(Text(e, "operand"),
        new(Cut(m).ProcessorId, Cut(m).StreamId.MachineId, Period(m), Text(e, "key")), Cut(m), (MetricDimension)e.GetProperty("dimension").GetInt32(),
        Number(Text(e, "value")), Text(e, "unit"), e.GetProperty("count").GetInt64(), Time(Text(e, "first")), Time(Text(e, "last")));

    private static ContainmentWitnessDocument Witness(JsonElement e, JsonElement m)
    {
        var f = e.GetProperty("fact");
        var fact = new DurableMetricInputFact
        {
            Id = new(Text(f, "FactId")), Key = Text(f, "MetricInputKey"), Value = Number(Text(f, "MetricValue")), Unit = Text(f, "Unit"),
            StartsAtUtc = Time(Text(f, "StartsAtUtc")), EndsAtUtc = Time(Text(f, "EndsAtUtc")), CompanyId = new(Text(f, "CompanyId")), SiteId = new(Text(f, "SiteId")),
            ProductionLineId = Optional(f, "ProductionLineId") is { } line ? new(line) : null, MachineId = new(Guid.Parse(Text(f, "MachineId"))),
            ShiftId = new(Text(f, "ShiftId")), ShiftScheduleAssignmentId = new(Text(f, "ShiftScheduleAssignmentId")),
            ProductionContextAssignmentId = Optional(f, "ProductionContextAssignmentId") is { } context ? new(context) : null,
            ProductionOrderId = Optional(f, "ProductionOrderId") is { } order ? new(order) : null, OperationId = Optional(f, "OperationId") is { } operation ? new(operation) : null,
            PartId = Optional(f, "PartId") is { } part ? new(part) : null, OperatorId = Optional(f, "OperatorId") is { } op ? new(op) : null,
            IsPlannedProductionTime = f.GetProperty("IsPlannedProductionTime").GetBoolean(),
            PlannedProductionScheduleAssignmentId = Optional(f, "PlannedProductionScheduleAssignmentId") is { } plan ? new(plan) : null,
            SourceContextualizedActivityIntervalId = Optional(f, "SourceContextualizedActivityIntervalId") is { } activity ? new(activity) : null,
            SourceEligibilityIntervalId = Optional(f, "SourceEligibilityIntervalId") is { } eligibility ? new(eligibility) : null,
            SourceQuantityEvidenceId = Optional(f, "SourceQuantityEvidenceId") is { } quantity ? new(quantity) : null
        };
        var shift = new ShiftOccurrenceId(new(Text(f, "OccurrenceSiteId")), new(Text(f, "OccurrenceShiftScheduleAssignmentId")), new(Text(f, "OccurrenceShiftId")),
            Time(Text(f, "OccurrenceStartsAtUtc")), Time(Text(f, "OccurrenceEndsAtUtc")));
        var day = new ProductionDayId(new(Text(f, "ProductionDaySiteId")), DateOnly.Parse(Text(f, "ProductionBusinessDate"), CultureInfo.InvariantCulture));
        return new(Cut(m), Period(m), Component(e.GetProperty("numerator"), m), Component(e.GetProperty("denominator"), m),
            new(Cut(m).StreamId, new(f.GetProperty("Position").GetUInt64()), fact, shift, day));
    }

    private static ContainmentResultDocument Result(JsonElement e, JsonElement m)
    {
        var metric = Text(e, "metric"); var key = new OperationalMetricEvaluationKey(Cut(m).StreamId.MachineId, Period(m), new(metric, "1.0"), new());
        ContainmentHistoricalAuthority? authority = null;
        if (e.GetProperty("authority").ValueKind != JsonValueKind.Null)
        {
            var a = e.GetProperty("authority"); authority = new(a.GetProperty("completedR").GetInt64(),
                a.GetProperty("producedSourceIds").EnumerateArray().Select(p => p.GetString()!), a.GetProperty("standardRevisions").EnumerateArray().Select(p => p.GetInt64()));
        }
        return new(new(Text(m, "projectionProcessor")), key, (OperationalMetricEvaluationStatus)e.GetProperty("status").GetInt32(),
            Optional(e, "value") is { } value ? Number(value) : null, OperationalMetricUnits.Ratio,
            e.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : (OperationalMetricEvaluationReasonCode)e.GetProperty("reason").GetInt32(),
            Optional(e, "reasonOperand"), Cut(m), authority, e.GetProperty("witness").ValueKind == JsonValueKind.Null ? null : Witness(e.GetProperty("witness"), m),
            e.GetProperty("components").EnumerateArray().Select(c => Component(c, m)), e.GetProperty("dependencies").EnumerateArray().Select(d =>
                new ContainmentDependencyDocument(d[0].GetString()!, new(Text(d[1], "metric"), "1.0"), Result(d[1], m))));
    }
}
