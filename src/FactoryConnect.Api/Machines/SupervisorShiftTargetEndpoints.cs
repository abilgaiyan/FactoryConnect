using System.Data;
using FactoryConnect.Api.Commissioning;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Api.Machines;

public sealed record LocalShiftTargetRequest(
    Guid TargetRevisionId, string ShiftScheduleAssignmentId, string ShiftId,
    DateTimeOffset ShiftStartsAtUtc, DateTimeOffset ShiftEndsAtUtc,
    int RevisionNumber, Guid? ExpectedHeadRevisionId, decimal TargetQuantity,
    string QuantityUnit, string? ProductionOrder, string? PartNumber,
    string? Operation, string? RevisionReason);

/// <summary>Local commissioning-only planning target endpoints; not OEE publication.</summary>
public static class SupervisorShiftTargetEndpoints
{
    public static IEndpointRouteBuilder MapSupervisorShiftTargetEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/shift-targets",
            ReadAsync).WithName("ReadLocalSupervisorShiftTargets")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapPost("/api/supervisor/v1/machines/{machineId:guid}/shift-targets",
            CreateAsync).WithName("CreateLocalSupervisorShiftTargetRevision")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/quantity-source-cut",
            ReadQuantitySourceCutAsync).WithName("ReadLocalSupervisorQuantitySourceCut")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/production-entries/{entryId:guid}/qualification-readiness",
            ReadQualificationReadinessAsync).WithName("ReadSupervisorQualificationReadiness")
            .WithGroupName("supervisor-local-v1");
        endpoints.MapGet("/api/supervisor/v1/machines/{machineId:guid}/accepted-shift-production",
            ReadAcceptedShiftProductionAsync).WithName("ReadLocalAcceptedShiftProduction")
            .WithGroupName("supervisor-local-v1");
        return endpoints;
    }

    /// <summary>Read-only local diagnostics. Never grants publication authority.</summary>
    private static async Task<IResult> ReadQualificationReadinessAsync(
        Guid machineId, Guid entryId, HttpContext context,
        IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        if (machineId == Guid.Empty || entryId == Guid.Empty)
            return Results.BadRequest(new { reason = "InvalidSupervisorEntryIdentity" });
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "dbo.InspectSupervisorProductionEntryReadiness", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = entryId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Results.Problem(statusCode: 503, title: "Readiness inspection returned no result");
            var actualMachine = reader.IsDBNull(reader.GetOrdinal("MachineId"))
                ? (Guid?)null : reader.GetGuid(reader.GetOrdinal("MachineId"));
            if (actualMachine is not null && actualMachine != machineId)
                return Results.NotFound();
            var status = reader.GetString(reader.GetOrdinal("ReadinessStatus"));
            // This endpoint intentionally never exposes a positive publication decision.
            return Results.Ok(new
            {
                classification = "SupervisorQualificationReadinessDiagnosticOnly",
                machineId,
                entryId,
                currentEvidenceId = reader.IsDBNull(reader.GetOrdinal("CurrentEvidenceId"))
                    ? (Guid?)null : reader.GetGuid(reader.GetOrdinal("CurrentEvidenceId")),
                sourceKind = reader.IsDBNull(reader.GetOrdinal("SourceKind"))
                    ? null : reader.GetString(reader.GetOrdinal("SourceKind")),
                localReviewDecision = reader.IsDBNull(reader.GetOrdinal("LocalReviewDecision"))
                    ? null : reader.GetString(reader.GetOrdinal("LocalReviewDecision")),
                readinessStatus = status,
                publicationAuthorized = false,
                warning = "Read-only local inspection. CNC coverage, population reconciliation and independent factory authorization are not established."
            });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Supervisor readiness SQL inspection unavailable");
        }
    }

    private static async Task<IResult> ReadAcceptedShiftProductionAsync(
        Guid machineId, string assignmentId, string shiftId,
        DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");
        try
        {
            var publication = await new SqlServerAcceptedShiftProductionReader(connectionString)
                .ReadAsync(machineId, assignmentId, shiftId, shiftStartsAtUtc,
                    shiftEndsAtUtc, cancellationToken);
            if (publication is null)
                return Results.Ok(new { classification = "AcceptedProductionUnavailable",
                    reason = "NoQualifiedPublicationForShift" });
            return Results.Ok(new
            {
                classification = "AcceptedShiftProduction",
                publication.PublicationId,
                publication.MachineId,
                publication.ShiftScheduleAssignmentId,
                publication.ShiftId,
                publication.ShiftStartsAtUtc,
                publication.ShiftEndsAtUtc,
                publication.ProductionOrder,
                publication.PartNumber,
                publication.Operation,
                publication.QuantityUnit,
                publication.AcceptedProducedQuantity,
                publication.Coverage,
                publication.AuthorityReference,
                publication.PublishedAtUtc
            });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftOccurrence" });
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(statusCode: 409,
                title: "Accepted production publication inconsistent");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503,
                title: "Accepted production SQL projection unavailable");
        }
    }

    private static async Task<IResult> ReadQuantitySourceCutAsync(
        Guid machineId, string siteId, string assignmentId, string shiftId,
        DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        string aggregationProcessorId, string streamKey, ulong position,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");
        try
        {
            var machine = new MachineId(machineId);
            var checkpoint = new MetricAggregationCheckpoint(
                new MetricAggregationProcessorId(aggregationProcessorId),
                new MetricInputStreamId(machine, streamKey),
                new MetricInputPosition(position));
            var occurrence = new ShiftOccurrenceId(
                new SiteId(siteId), new ShiftScheduleAssignmentId(assignmentId),
                new ShiftId(shiftId), shiftStartsAtUtc, shiftEndsAtUtc);
            var cut = await new SqlServerProductionQuantitySourceInventoryReader(connectionString)
                .ReadAsync(checkpoint, machine, occurrence, cancellationToken);
            return Results.Ok(new
            {
                classification = "HistoricalMetricInputSourceInventoryNotQualifiedActual",
                machineId,
                shift = new { siteId, assignmentId, shiftId, shiftStartsAtUtc, shiftEndsAtUtc },
                sourceRevision = new { aggregationProcessorId, streamKey, position = position.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                sourceCount = cut.Sources.Count,
                sources = cut.Sources.Select(source => new
                {
                    evidenceId = source.Evidence.Id.Value,
                    occurredAtUtc = source.Evidence.OccurredAtUtc,
                    partCountIncrement = source.Evidence.PartCountIncrement,
                    goodQuantity = source.Evidence.GoodQuantity,
                    rejectedQuantity = source.Evidence.RejectedQuantity,
                    productionOrderId = source.Evidence.ProductionOrderId?.Value,
                    partId = source.Evidence.PartId?.Value,
                    operationId = source.Evidence.OperationId?.Value,
                    contributingPositions = source.ContributingPositions.Select(item =>
                        item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()
                }).ToArray(),
                warning = "Source inventory only. Not accepted actual production, completeness, or target attainment."
            });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidQuantitySourceCutIdentity" });
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(statusCode: 409, title: "Quantity source cut unavailable or inconsistent");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Quantity source SQL read unavailable");
        }
    }

    private static async Task<IResult> ReadAsync(Guid machineId, string assignmentId,
        DateTimeOffset shiftStartsAtUtc, DateTimeOffset shiftEndsAtUtc,
        HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration))
            return Results.Problem(statusCode: 403, title: "Local commissioning access required");
        var connectionString = configuration["Commissioning:ReadOnlyConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Commissioning SQL reader not configured");
        try
        {
            var revisions = await new SqlServerSupervisorShiftTargetStore(connectionString)
                .ListAsync(machineId, assignmentId, shiftStartsAtUtc, shiftEndsAtUtc,
                    cancellationToken);
            return Results.Ok(revisions);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftTargetIdentity" });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Shift target SQL read unavailable");
        }
    }

    private static async Task<IResult> CreateAsync(Guid machineId, LocalShiftTargetRequest request,
        HttpContext context, IConfiguration configuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CommissioningAccess.Allows(context, configuration) ||
            !configuration.GetValue<bool>("SupervisorLocalShiftTarget:EnableWrites"))
            return Results.Problem(statusCode: 403, title: "Local shift target writes disabled");
        var actor = configuration["SupervisorLocalShiftTarget:OperatorId"];
        var connectionString = configuration["SupervisorLocalShiftTarget:WriteConnectionString"];
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem(statusCode: 503, title: "Local shift target writer not configured");
        var revision = new SupervisorShiftTargetRevision(
            request.TargetRevisionId, machineId, request.ShiftScheduleAssignmentId,
            request.ShiftId, request.ShiftStartsAtUtc, request.ShiftEndsAtUtc,
            request.RevisionNumber, request.TargetQuantity, request.QuantityUnit,
            request.ProductionOrder, request.PartNumber, request.Operation,
            request.RevisionReason, actor, clock.GetUtcNow());
        try
        {
            var inserted = await new SqlServerSupervisorShiftTargetStore(connectionString)
                .AppendAsync(revision, request.ExpectedHeadRevisionId, cancellationToken);
            return inserted
                ? Results.Created($"/api/supervisor/v1/machines/{machineId:D}/shift-targets",
                    revision)
                : Results.Ok(new { revisionId = request.TargetRevisionId, alreadyExists = true });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { reason = "InvalidShiftTarget" });
        }
        catch (SqlException exception) when (exception.Number is 51100 or 51101 or 51102 or 51103 or 2601 or 2627)
        {
            return Results.Problem(statusCode: 409, title: "Shift target roster or revision conflict");
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Shift target SQL write unavailable");
        }
    }
}
