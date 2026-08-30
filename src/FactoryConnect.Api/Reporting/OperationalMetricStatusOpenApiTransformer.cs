using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace FactoryConnect.Api.Reporting;

internal sealed class OperationalMetricStatusOpenApiTransformer : IOpenApiSchemaTransformer
{
    private static readonly IReadOnlyList<IOpenApiAny> StatusValues =
    [
        new OpenApiString("calculated"),
        new OpenApiString("unavailable"),
        new OpenApiString("insufficient-evidence"),
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
            ApplyEnum(schema.Properties["status"]);
        }
        else if (context.JsonTypeInfo.Type == typeof(ShiftOperationalMetricQueryRequest)
            || context.JsonTypeInfo.Type == typeof(ProductionDayOperationalMetricQueryRequest))
        {
            ApplyEnum(schema.Properties["statuses"].Items);
        }

        return Task.CompletedTask;
    }

    private static void ApplyEnum(OpenApiSchema? schema)
    {
        if (schema is null)
        {
            return;
        }

        schema.Enum = StatusValues;
    }
}
