using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FactoryConnect.Api.Reporting;

internal sealed class OperationalMetricStatusOpenApiTransformer : IOpenApiSchemaTransformer
{
    private static readonly IList<JsonNode> StatusValues =
    [
        JsonValue.Create("calculated")!,
        JsonValue.Create("unavailable")!,
        JsonValue.Create("insufficient-evidence")!,
    ];

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
                ApplyEnum(statusSchema);
            }
        }
        else if (context.JsonTypeInfo.Type == typeof(ShiftOperationalMetricQueryRequest)
            || context.JsonTypeInfo.Type == typeof(ProductionDayOperationalMetricQueryRequest))
        {
            if (schema.Properties?.TryGetValue("statuses", out var statusesSchema) == true)
            {
                ApplyEnum(statusesSchema.Items);
            }
        }

        return Task.CompletedTask;
    }

    private static void ApplyEnum(IOpenApiSchema? schema)
    {
        if (schema is OpenApiSchema concreteSchema)
        {
            concreteSchema.Enum = StatusValues;
        }
    }
}
