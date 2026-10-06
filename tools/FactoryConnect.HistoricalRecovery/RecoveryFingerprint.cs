using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryConnect.HistoricalRecovery;

/// <summary>V1: UTF8 JSON, ordinal object properties, ordered arrays, exact decimal values, no whitespace.</summary>
public static class RecoveryFingerprint
{
    public static JsonElement Payload(ReconstructionRecord record, object projections) => RecoveryJson.Element(new
    {
        EncodingVersion = "FC-P0D-1", Processor = RecoveryTargets.Processor, record.Target,
        record.DefinitionAuthority, record.Definitions,
        HistoricalEvidence = new { record.Capture.Revision, record.Capture.PrefixMembership,
            record.Capture.PrefixFacts, record.Capture.Snapshot, record.Capture.Transition,
            record.Capture.QuantitySources, record.Capture.ReferenceTimeCut, record.Capture.StandardCut },
        record.ReconstructedPeriodAggregates, record.Evaluation1, Projections = projections,
    });

    public static string Compute(JsonElement payload) => Convert.ToHexString(SHA256.HashData(CanonicalBytes(payload)));
    public static bool Matches(JsonElement payload, string expected) =>
        expected.Length == 64 && string.Equals(Compute(payload), expected, StringComparison.Ordinal);
    public static byte[] CanonicalBytes(JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, payload);
        return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new InvalidDataException("Duplicate canonical payload property.");
                foreach (var property in properties) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture)); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("Unsupported canonical payload value.");
        }
    }
}
