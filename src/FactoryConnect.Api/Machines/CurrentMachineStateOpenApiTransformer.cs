using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FactoryConnect.Api.Machines;

internal sealed class CurrentMachineStateOpenApiTransformer : IOpenApiSchemaTransformer
{
    private static readonly JsonNode[] OutcomeValues =
        ToEnumValues(CurrentMachineStateHttpVocabulary.Outcomes);
    private static readonly JsonNode[] CoverageValues =
        ToEnumValues(CurrentMachineStateHttpVocabulary.Coverages);
    private static readonly JsonNode[] MachineStateValues =
        ToEnumValues(CurrentMachineStateHttpVocabulary.MachineStates);
    private static readonly JsonNode[] FreshnessValues =
        ToEnumValues(CurrentMachineStateHttpVocabulary.FreshnessValues);
    private static readonly JsonNode[] UsabilityValues =
        ToEnumValues(CurrentMachineStateHttpVocabulary.UsabilityValues);

    public async Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.JsonTypeInfo.Type == typeof(CurrentMachineStateResponse))
        {
            ApplyPropertyEnum(schema, "outcome", OutcomeValues);
            ApplyPropertyEnum(schema, "coverage", CoverageValues);
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("evidence");
            await MakeEvidencePropertyNullableAsync(
                    schema,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (context.JsonTypeInfo.Type == typeof(CurrentMachineStateEvidenceResponse))
        {
            ApplyPropertyEnum(schema, "machineState", MachineStateValues);
            ApplyPropertyEnum(schema, "freshness", FreshnessValues);
            ApplyPropertyEnum(schema, "usability", UsabilityValues);
        }

    }

    private static JsonNode[] ToEnumValues(IEnumerable<string> values) =>
        values.Select(static value => JsonValue.Create(value)!).Cast<JsonNode>().ToArray();

    private static async Task MakeEvidencePropertyNullableAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Properties?.ContainsKey("evidence") != true)
        {
            return;
        }

        var evidenceSchema = await context
            .GetOrCreateSchemaAsync(
                typeof(CurrentMachineStateEvidenceResponse),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        schema.Properties["evidence"] = new OpenApiSchema
        {
            OneOf =
            [
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Null,
                },
                evidenceSchema,
            ],
        };
    }

    private static void ApplyPropertyEnum(
        OpenApiSchema schema,
        string propertyName,
        JsonNode[] values)
    {
        if (schema.Properties?.TryGetValue(propertyName, out var propertySchema) == true
            && propertySchema is OpenApiSchema concreteSchema)
        {
            concreteSchema.Enum = values;
        }
    }
}
