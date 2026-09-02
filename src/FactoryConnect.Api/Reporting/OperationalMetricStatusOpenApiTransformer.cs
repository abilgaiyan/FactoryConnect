using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FactoryConnect.Api.Reporting;

internal sealed class OperationalMetricStatusOpenApiTransformer : IOpenApiSchemaTransformer
{
    private static readonly JsonNode[] StatusValues = ToEnumValues(OperationalMetricHttpVocabulary.Statuses);
    private static readonly JsonNode[] OrderValues = ToEnumValues(OperationalMetricHttpVocabulary.Orders);
    private static readonly JsonNode[] ScopeValues = ToEnumValues(OperationalMetricHttpVocabulary.Scopes);

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.JsonTypeInfo.Type == typeof(OperationalMetricItemResponse))
        {
            ApplyPropertyEnum(schema, "status", StatusValues);
            ApplyPropertyEnum(schema, "scope", ScopeValues);
        }
        else if (context.JsonTypeInfo.Type == typeof(ProductionDayShiftMetricResponse))
        {
            ApplyPropertyEnum(schema, "status", StatusValues);
        }
        else if (context.JsonTypeInfo.Type == typeof(ShiftOperationalMetricQueryRequest)
            || context.JsonTypeInfo.Type == typeof(ProductionDayOperationalMetricQueryRequest))
        {
            ApplyArrayItemEnum(schema, "statuses", StatusValues);
            ApplyPropertyEnum(schema, "order", OrderValues);
        }
        else if (context.JsonTypeInfo.Type == typeof(ProductionDayShiftOperationalMetricQueryRequest))
        {
            ApplyArrayItemEnum(schema, "statuses", StatusValues);
        }

        return Task.CompletedTask;
    }

    private static JsonNode[] ToEnumValues(IEnumerable<string> values) =>
        values
            .Select(static value => JsonValue.Create(value)!)
            .Cast<JsonNode>()
            .ToArray();

    private static void ApplyPropertyEnum(
        OpenApiSchema schema,
        string propertyName,
        JsonNode[] values)
    {
        if (schema.Properties?.TryGetValue(propertyName, out var propertySchema) == true)
        {
            ApplyEnum(propertySchema, values);
        }
    }

    private static void ApplyArrayItemEnum(
        OpenApiSchema schema,
        string propertyName,
        JsonNode[] values)
    {
        if (schema.Properties?.TryGetValue(propertyName, out var propertySchema) == true)
        {
            ApplyEnum(propertySchema.Items, values);
        }
    }

    private static void ApplyEnum(IOpenApiSchema? schema, JsonNode[] values)
    {
        if (schema is OpenApiSchema concreteSchema)
        {
            concreteSchema.Enum = values;
        }
    }
}
