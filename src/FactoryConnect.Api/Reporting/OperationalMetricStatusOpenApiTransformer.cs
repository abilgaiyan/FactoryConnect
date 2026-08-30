using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FactoryConnect.Api.Reporting;

internal sealed class OperationalMetricStatusOpenApiTransformer : IOpenApiSchemaTransformer
{
    private static readonly IList<JsonNode> StatusValues = ToEnumValues(OperationalMetricHttpVocabulary.Statuses);
    private static readonly IList<JsonNode> OrderValues = ToEnumValues(OperationalMetricHttpVocabulary.Orders);
    private static readonly IList<JsonNode> ScopeValues = ToEnumValues(OperationalMetricHttpVocabulary.Scopes);

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.JsonTypeInfo.Type == typeof(OperationalMetricItemResponse))
        {
            if (schema.Properties?.TryGetValue("status", out var statusSchema) == true)
            {
                ApplyEnum(statusSchema, StatusValues);
            }

            if (schema.Properties?.TryGetValue("scope", out var scopeSchema) == true)
            {
                ApplyEnum(scopeSchema, ScopeValues);
            }
        }
        else if (context.JsonTypeInfo.Type == typeof(ShiftOperationalMetricQueryRequest)
            || context.JsonTypeInfo.Type == typeof(ProductionDayOperationalMetricQueryRequest))
        {
            if (schema.Properties?.TryGetValue("statuses", out var statusesSchema) == true)
            {
                ApplyEnum(statusesSchema.Items, StatusValues);
            }

            if (schema.Properties?.TryGetValue("order", out var orderSchema) == true)
            {
                ApplyEnum(orderSchema, OrderValues);
            }
        }

        return Task.CompletedTask;
    }

    private static IList<JsonNode> ToEnumValues(IEnumerable<string> values) =>
        values
            .Select(static value => JsonValue.Create(value)!)
            .Cast<JsonNode>()
            .ToArray();

    private static void ApplyEnum(IOpenApiSchema? schema, IList<JsonNode> values)
    {
        if (schema is OpenApiSchema concreteSchema)
        {
            concreteSchema.Enum = values;
        }
    }
}
