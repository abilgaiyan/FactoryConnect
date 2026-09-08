using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Reads and validates the frozen FC-030.2A DependencySnapshotBinary V1 (FCDS) representation.
/// </summary>
internal static class OperationalMetricDependencySnapshotV1Decoder
{
    private const int MaximumDepth = 64;
    private const int MaximumEvidenceItemsPerKind = 65_535;
    private const int MaximumTotalEvidenceNodeCount = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static OperationalMetricProjection Decode(ReadOnlySpan<byte> encodedSnapshot)
    {
        if (encodedSnapshot.Length == 0 ||
            encodedSnapshot.Length > OperationalMetricDependencySnapshotV1Codec.MaximumEncodedLength)
        {
            throw Corrupt("dependency snapshot length");
        }

        var state = new DecodingState();
        var reader = new BinaryReader(encodedSnapshot.ToArray());
        var projection = ReadProjection(reader, depth: 1, state);
        reader.RequireEnd();

        var canonical = OperationalMetricDependencySnapshotV1Codec.Encode(projection);
        if (!canonical.AsSpan().SequenceEqual(encodedSnapshot))
        {
            throw Corrupt("dependency snapshot canonical representation");
        }

        return projection;
    }

    private static OperationalMetricProjection ReadProjection(
        BinaryReader reader,
        int depth,
        DecodingState state)
    {
        if (depth > MaximumDepth)
        {
            throw Corrupt("dependency snapshot recursive depth");
        }

        reader.RequireBytes("FCDS"u8);
        if (reader.ReadByte() != 0x02 ||
            reader.ReadUInt16() != OperationalMetricDependencySnapshotV1Codec.CodecVersion)
        {
            throw Corrupt("dependency snapshot envelope");
        }

        var processorId = new OperationalMetricProjectionProcessorId(reader.ReadRequiredString());
        var key = DecodeEvaluationKey(reader.ReadBlob(OperationalMetricEvaluationKeyV1Codec.MaximumEncodedLength));
        var statusValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(OperationalMetricEvaluationStatus), (int)statusValue))
        {
            throw Corrupt("dependency projection status");
        }

        var value = reader.ReadOptionalDecimal();
        var unit = reader.ReadRequiredString();
        var reasonCode = reader.ReadOptionalReason();
        var reasonOperandName = reader.ReadOptionalString();
        var sourceRevision = ReadSourceRevision(reader);

        var componentCount = reader.ReadCount(MaximumEvidenceItemsPerKind);
        var components = new OperationalMetricComponentProjectionEvidence[componentCount];
        for (var index = 0; index < componentCount; index++)
        {
            state.AddEvidenceNode();
            components[index] = ReadComponentEvidence(reader);
        }

        var dependencyCount = reader.ReadCount(MaximumEvidenceItemsPerKind);
        var dependencies = new OperationalMetricDependencyProjectionEvidence[dependencyCount];
        for (var index = 0; index < dependencyCount; index++)
        {
            state.AddEvidenceNode();
            dependencies[index] = ReadDependencyEvidence(reader, depth, state);
        }

        try
        {
            return new OperationalMetricProjection(
                processorId,
                key,
                (OperationalMetricEvaluationStatus)statusValue,
                value,
                unit,
                reasonCode,
                reasonOperandName,
                sourceRevision,
                components,
                dependencies);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                "Persisted operational metric dependency snapshot is corrupt.",
                exception);
        }
    }

    private static OperationalMetricComponentProjectionEvidence ReadComponentEvidence(BinaryReader reader)
    {
        var operandName = reader.ReadRequiredString();
        var machineId = new MachineId(reader.ReadGuid());
        var periodId = ReadPeriod(reader);
        var componentKey = reader.ReadRequiredString();
        var sourceRevision = ReadSourceRevision(reader);
        var dimensionValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(MetricDimension), (int)dimensionValue))
        {
            throw Corrupt("dependency component dimension");
        }

        var value = reader.ReadRequiredDecimal();
        var unit = reader.ReadRequiredString();
        var inputCountValue = reader.ReadUInt64();
        if (inputCountValue == 0 || inputCountValue > long.MaxValue)
        {
            throw Corrupt("dependency component input count");
        }

        var firstInputTimestamp = reader.ReadUtcTimestamp();
        var lastInputTimestamp = reader.ReadUtcTimestamp();

        try
        {
            return new OperationalMetricComponentProjectionEvidence(
                operandName,
                new OperationalMetricAggregateSourceIdentity(
                    sourceRevision.ProcessorId,
                    machineId,
                    periodId,
                    componentKey),
                sourceRevision,
                (MetricDimension)dimensionValue,
                value,
                unit,
                checked((long)inputCountValue),
                firstInputTimestamp,
                lastInputTimestamp);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                "Persisted operational metric dependency component evidence is corrupt.",
                exception);
        }
    }

    private static OperationalMetricDependencyProjectionEvidence ReadDependencyEvidence(
        BinaryReader reader,
        int parentDepth,
        DecodingState state)
    {
        var operandName = reader.ReadRequiredString();
        var definitionId = new OperationalMetricDefinitionId(
            reader.ReadRequiredString(),
            reader.ReadRequiredString());
        var nestedBytes = reader.ReadBlob(OperationalMetricDependencySnapshotV1Codec.MaximumEncodedLength);
        var nestedReader = new BinaryReader(nestedBytes);
        var projection = ReadProjection(nestedReader, checked(parentDepth + 1), state);
        nestedReader.RequireEnd();

        try
        {
            return new OperationalMetricDependencyProjectionEvidence(
                operandName,
                definitionId,
                projection);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Persisted operational metric dependency evidence is corrupt.",
                exception);
        }
    }

    private static MetricAggregationCheckpoint ReadSourceRevision(BinaryReader reader)
    {
        var processorId = new MetricAggregationProcessorId(reader.ReadRequiredString());
        var machineId = new MachineId(reader.ReadGuid());
        var streamKey = reader.ReadRequiredString();
        var position = reader.ReadUInt64();
        return new MetricAggregationCheckpoint(
            processorId,
            new MetricInputStreamId(machineId, streamKey),
            new MetricInputPosition(position));
    }

    private static OperationalMetricPeriodId ReadPeriod(BinaryReader reader) =>
        reader.ReadByte() switch
        {
            0x01 => new OperationalMetricPeriodId.Shift(
                new ShiftOccurrenceId(
                    new SiteId(reader.ReadRequiredString()),
                    new ShiftScheduleAssignmentId(reader.ReadRequiredString()),
                    new ShiftId(reader.ReadRequiredString()),
                    reader.ReadUtcTimestamp(),
                    reader.ReadUtcTimestamp())),
            0x02 => new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(
                    new SiteId(reader.ReadRequiredString()),
                    DateOnly.FromDayNumber(reader.ReadInt32()))),
            _ => throw Corrupt("dependency period kind"),
        };

    private static OperationalMetricEvaluationKey DecodeEvaluationKey(ReadOnlySpan<byte> encodedKey)
    {
        try
        {
            var reader = new BinaryReader(encodedKey.ToArray());
            reader.RequireBytes("FCEK"u8);
            if (reader.ReadByte() != 0x01 ||
                reader.ReadUInt16() != OperationalMetricEvaluationKeyV1Codec.CodecVersion)
            {
                throw Corrupt("dependency evaluation-key envelope");
            }

            var machineId = new MachineId(reader.ReadGuid());
            var period = ReadPeriod(reader);
            var productionOrderId = reader.ReadOptionalString();
            var operationId = reader.ReadOptionalString();
            var partId = reader.ReadOptionalString();
            var operatorId = reader.ReadOptionalString();
            var metricKey = reader.ReadRequiredString();
            var version = reader.ReadRequiredString();
            reader.RequireEnd();

            var context = new OperationalMetricEvaluationContextKey
            {
                ProductionOrderId = productionOrderId is null ? null : new ProductionOrderId(productionOrderId),
                OperationId = operationId is null ? null : new OperationId(operationId),
                PartId = partId is null ? null : new PartId(partId),
                OperatorId = operatorId is null ? null : new OperatorId(operatorId),
            };

            var key = new OperationalMetricEvaluationKey(
                machineId,
                period,
                new OperationalMetricDefinitionId(metricKey, version),
                context);

            if (!OperationalMetricEvaluationKeyV1Codec.Encode(key).AsSpan().SequenceEqual(encodedKey))
            {
                throw Corrupt("dependency evaluation-key canonical representation");
            }

            return key;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidOperationException(
                "Persisted operational metric dependency evaluation key is corrupt.",
                exception);
        }
    }

    private static InvalidOperationException Corrupt(string field) =>
        new($"Persisted operational metric {field} is corrupt.");

    private sealed class DecodingState
    {
        private int _totalEvidenceNodeCount;

        public void AddEvidenceNode()
        {
            _totalEvidenceNodeCount = checked(_totalEvidenceNodeCount + 1);
            if (_totalEvidenceNodeCount > MaximumTotalEvidenceNodeCount)
            {
                throw Corrupt("dependency snapshot total evidence node count");
            }
        }
    }

    private sealed class BinaryReader
    {
        private readonly byte[] _bytes;
        private int _offset;

        public BinaryReader(byte[] bytes)
        {
            _bytes = bytes;
        }

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _bytes[_offset++];
        }

        public ushort ReadUInt16()
        {
            var span = ReadSpan(2);
            return BinaryPrimitives.ReadUInt16BigEndian(span);
        }

        public uint ReadUInt32()
        {
            var span = ReadSpan(4);
            return BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        public ulong ReadUInt64()
        {
            var span = ReadSpan(8);
            return BinaryPrimitives.ReadUInt64BigEndian(span);
        }

        public int ReadInt32()
        {
            var span = ReadSpan(4);
            return BinaryPrimitives.ReadInt32BigEndian(span);
        }

        public long ReadInt64()
        {
            var span = ReadSpan(8);
            return BinaryPrimitives.ReadInt64BigEndian(span);
        }

        public Guid ReadGuid() => new(ReadSpan(16), bigEndian: true);

        public DateTimeOffset ReadUtcTimestamp()
        {
            var ticks = ReadInt64();
            try
            {
                return new DateTimeOffset(ticks, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidOperationException(
                    "Persisted operational metric UTC timestamp is corrupt.",
                    exception);
            }
        }

        public int ReadCount(int maximum)
        {
            var value = ReadUInt32();
            if (value > maximum)
            {
                throw Corrupt("dependency evidence count");
            }

            return checked((int)value);
        }

        public byte[] ReadBlob(int maximumLength)
        {
            var length = ReadUInt32();
            if (length == 0 || length > maximumLength || length > int.MaxValue)
            {
                throw Corrupt("dependency binary field length");
            }

            return ReadSpan(checked((int)length)).ToArray();
        }

        public string ReadRequiredString()
        {
            var value = ReadString();
            if (value.Length == 0)
            {
                throw Corrupt("dependency required string");
            }

            return value;
        }

        public string? ReadOptionalString() =>
            ReadByte() switch
            {
                0x00 => null,
                0x01 => ReadRequiredString(),
                _ => throw Corrupt("dependency optional string marker"),
            };

        public decimal ReadRequiredDecimal()
        {
            var text = ReadRequiredString();
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !string.Equals(CanonicalDecimalTextV1Codec.Serialize(value), text, StringComparison.Ordinal))
            {
                throw Corrupt("dependency decimal representation");
            }

            return value;
        }

        public decimal? ReadOptionalDecimal() =>
            ReadByte() switch
            {
                0x00 => null,
                0x01 => ReadRequiredDecimal(),
                _ => throw Corrupt("dependency optional decimal marker"),
            };

        public OperationalMetricEvaluationReasonCode? ReadOptionalReason()
        {
            var marker = ReadByte();
            if (marker == 0x00)
            {
                return null;
            }

            if (marker != 0x01)
            {
                throw Corrupt("dependency optional reason marker");
            }

            var value = ReadByte();
            if (!Enum.IsDefined(typeof(OperationalMetricEvaluationReasonCode), (int)value))
            {
                throw Corrupt("dependency reason code");
            }

            return (OperationalMetricEvaluationReasonCode)value;
        }

        public void RequireBytes(ReadOnlySpan<byte> expected)
        {
            if (!ReadSpan(expected.Length).SequenceEqual(expected))
            {
                throw Corrupt("dependency binary marker");
            }
        }

        public void RequireEnd()
        {
            if (_offset != _bytes.Length)
            {
                throw Corrupt("dependency snapshot trailing bytes");
            }
        }

        private string ReadString()
        {
            var length = ReadUInt32();
            if (length > 768 || length > int.MaxValue)
            {
                throw Corrupt("dependency string length");
            }

            string value;
            try
            {
                value = StrictUtf8.GetString(ReadSpan(checked((int)length)));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException(
                    "Persisted operational metric dependency string is corrupt.",
                    exception);
            }

            if (value.Length > 256)
            {
                throw Corrupt("dependency string UTF-16 length");
            }

            return value;
        }

        private ReadOnlySpan<byte> ReadSpan(int length)
        {
            EnsureAvailable(length);
            var span = _bytes.AsSpan(_offset, length);
            _offset = checked(_offset + length);
            return span;
        }

        private void EnsureAvailable(int length)
        {
            if (length < 0 || _offset > _bytes.Length - length)
            {
                throw Corrupt("dependency snapshot truncation");
            }
        }
    }
}
