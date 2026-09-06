using System.Collections.Immutable;

namespace FactoryConnect.Persistence.SqlServer;

internal static class SqlRepositoryPost005CatalogAuthority
{
    public static SqlSchemaDescriptor Apply(SqlSchemaDescriptor descriptor) => new(
        descriptor.Tables.Select(RewriteTable).ToImmutableArray());

    private static SqlTableDescriptor RewriteTable(SqlTableDescriptor table)
    {
        if (!CheckDefinitions.Keys.Any(key => key.Table == table.Name.ObjectName) &&
            !FilterDefinitions.Keys.Any(key => key.Table == table.Name.ObjectName) &&
            !string.Equals(table.Name.ObjectName, "OperationalMetricProjection", StringComparison.Ordinal))
        {
            return table;
        }

        var columns = table.Columns
            .Select(column => RewriteColumn(table.Name.ObjectName, column))
            .ToImmutableArray();
        var checks = table.CheckConstraints
            .Select(check => RewriteCheck(table.Name.ObjectName, check))
            .ToImmutableArray();
        var indexes = table.Indexes
            .Select(index => RewriteIndex(table.Name.ObjectName, index))
            .ToImmutableArray();

        return table with
        {
            Columns = columns,
            CheckConstraints = checks,
            Indexes = indexes,
        };
    }

    private static SqlColumnDescriptor RewriteColumn(string tableName, SqlColumnDescriptor column)
    {
        if (string.Equals(tableName, "OperationalMetricProjection", StringComparison.Ordinal) &&
            string.Equals(column.Name, "MachineOrderKey", StringComparison.Ordinal))
        {
            return column with
            {
                Computed = new SqlComputedDescriptor(
                    "(CONVERT([binary](16),replace(CONVERT([char](36),[MachineId]),'-',''),(2)))",
                    IsPersisted: true),
            };
        }

        return column;
    }

    private static SqlCheckConstraintDescriptor RewriteCheck(
        string tableName,
        SqlCheckConstraintDescriptor check)
    {
        return CheckDefinitions.TryGetValue((tableName, check.Name), out var definition)
            ? check with { CanonicalDefinition = definition }
            : check;
    }

    private static SqlIndexDescriptor RewriteIndex(string tableName, SqlIndexDescriptor index)
    {
        if (!FilterDefinitions.TryGetValue((tableName, index.Name), out var filter))
        {
            return index;
        }

        return index with
        {
            IndexStructure = index.IndexStructure with
            {
                CanonicalFilterDefinition = filter,
            },
        };
    }

    private static readonly Dictionary<(string Table, string Name), string> FilterDefinitions = new()
    {
        [("OperationalMetricProjection", "IX_OperationalMetricProjection_ShiftWindow")] =
            "([PeriodKind]=(1))",
        [("OperationalMetricProjection", "IX_OperationalMetricProjection_ProductionDayWindow")] =
            "([PeriodKind]=(2))",
    };

    private static readonly Dictionary<(string Table, string Name), string> CheckDefinitions = new()
    {
        [("OperationalMetricProjectionProcessor", "CK_OperationalMetricProjectionProcessor_ProcessorKey")] =
            "(datalength([ProcessorKey])>(0))",
        [("OperationalMetricProjectionProcessor", "CK_OperationalMetricProjectionProcessor_ProcessorKeyBinary")] =
            "((datalength([ProcessorKeyBinary])>=(1) AND datalength([ProcessorKeyBinary])<=(769)))",

        [("OperationalMetricProjectionCheckpoint", "CK_OperationalMetricProjectionCheckpoint_Position")] =
            "([Position]>=(1) AND [Position]<=(18446744073709551615.))",

        [("OperationalMetricProjection", "CK_OperationalMetricProjection_EvaluationKeyCodecVersion")] =
            "([EvaluationKeyCodecVersion]=(1))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_EvaluationKeyBinaryLength")] =
            "(datalength([EvaluationKeyBinary])>=(1) AND datalength([EvaluationKeyBinary])<=(6992))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_PeriodShape")] =
            "([PeriodKind]=(1) AND [PeriodSiteId] IS NOT NULL AND datalength([PeriodSiteId])>(0) AND [PeriodSiteOrderKey] IS NOT NULL AND (datalength([PeriodSiteOrderKey])>=(1) AND datalength([PeriodSiteOrderKey])<=(769)) AND [ShiftScheduleAssignmentId] IS NOT NULL AND datalength([ShiftScheduleAssignmentId])>(0) AND [ShiftScheduleAssignmentOrderKey] IS NOT NULL AND (datalength([ShiftScheduleAssignmentOrderKey])>=(1) AND datalength([ShiftScheduleAssignmentOrderKey])<=(769)) AND [ShiftId] IS NOT NULL AND datalength([ShiftId])>(0) AND [ShiftOrderKey] IS NOT NULL AND (datalength([ShiftOrderKey])>=(1) AND datalength([ShiftOrderKey])<=(769)) AND [ShiftStartsAtUtc] IS NOT NULL AND [ShiftEndsAtUtc] IS NOT NULL AND datepart(tzoffset,[ShiftStartsAtUtc])=(0) AND datepart(tzoffset,[ShiftEndsAtUtc])=(0) AND [ShiftEndsAtUtc]>[ShiftStartsAtUtc] AND [ProductionBusinessDate] IS NULL OR [PeriodKind]=(2) AND [PeriodSiteId] IS NOT NULL AND datalength([PeriodSiteId])>(0) AND [PeriodSiteOrderKey] IS NOT NULL AND (datalength([PeriodSiteOrderKey])>=(1) AND datalength([PeriodSiteOrderKey])<=(769)) AND [ProductionBusinessDate] IS NOT NULL AND [ShiftScheduleAssignmentId] IS NULL AND [ShiftScheduleAssignmentOrderKey] IS NULL AND [ShiftId] IS NULL AND [ShiftOrderKey] IS NULL AND [ShiftStartsAtUtc] IS NULL AND [ShiftEndsAtUtc] IS NULL)",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_ProductionOrderContext")] =
            "([ProductionOrderPresent]=(0) AND [ProductionOrderId] IS NULL AND [ProductionOrderOrderKey] IS NULL OR [ProductionOrderPresent]=(1) AND [ProductionOrderId] IS NOT NULL AND datalength([ProductionOrderId])>(0) AND [ProductionOrderOrderKey] IS NOT NULL AND (datalength([ProductionOrderOrderKey])>=(1) AND datalength([ProductionOrderOrderKey])<=(769)))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_OperationContext")] =
            "([OperationPresent]=(0) AND [OperationId] IS NULL AND [OperationOrderKey] IS NULL OR [OperationPresent]=(1) AND [OperationId] IS NOT NULL AND datalength([OperationId])>(0) AND [OperationOrderKey] IS NOT NULL AND (datalength([OperationOrderKey])>=(1) AND datalength([OperationOrderKey])<=(769)))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_PartContext")] =
            "([PartPresent]=(0) AND [PartId] IS NULL AND [PartOrderKey] IS NULL OR [PartPresent]=(1) AND [PartId] IS NOT NULL AND datalength([PartId])>(0) AND [PartOrderKey] IS NOT NULL AND (datalength([PartOrderKey])>=(1) AND datalength([PartOrderKey])<=(769)))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_OperatorContext")] =
            "([OperatorPresent]=(0) AND [OperatorId] IS NULL AND [OperatorOrderKey] IS NULL OR [OperatorPresent]=(1) AND [OperatorId] IS NOT NULL AND datalength([OperatorId])>(0) AND [OperatorOrderKey] IS NOT NULL AND (datalength([OperatorOrderKey])>=(1) AND datalength([OperatorOrderKey])<=(769)))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_MetricIdentity")] =
            "(datalength([MetricKey])>(0) AND (datalength([MetricKeyOrderKey])>=(1) AND datalength([MetricKeyOrderKey])<=(769)) AND datalength([DefinitionVersion])>(0) AND (datalength([DefinitionVersionOrderKey])>=(1) AND datalength([DefinitionVersionOrderKey])<=(769)) AND datalength([Unit])>(0))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_SourceRevisionPosition")] =
            "([SourceRevisionPosition]>=(1) AND [SourceRevisionPosition]<=(18446744073709551615.))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_Status")] =
            "([Status]=(2) OR [Status]=(1) OR [Status]=(0))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_ReasonCode")] =
            "([ReasonCode] IS NULL OR ([ReasonCode]=(5) OR [ReasonCode]=(4) OR [ReasonCode]=(3) OR [ReasonCode]=(2) OR [ReasonCode]=(1) OR [ReasonCode]=(0)))",
        [("OperationalMetricProjection", "CK_OperationalMetricProjection_StatusShape")] =
            "([Status]=(0) AND [MetricValue] IS NOT NULL AND datalength([MetricValue])>(0) AND [ReasonCode] IS NULL AND [ReasonOperandName] IS NULL OR ([Status]=(2) OR [Status]=(1)) AND [MetricValue] IS NULL AND [ReasonCode] IS NOT NULL AND ([ReasonCode]=(5) OR [ReasonCode]=(4) OR [ReasonCode]=(3) OR [ReasonCode]=(2) OR [ReasonCode]=(1) OR [ReasonCode]=(0)) AND ([ReasonOperandName] IS NULL OR datalength([ReasonOperandName])>(0)))",

        [("OperationalMetricProjectionEvidence", "CK_OperationalMetricProjectionEvidence_Operand")] =
            "(datalength([OperandName])>(0) AND (datalength([OperandNameOrderKey])>=(1) AND datalength([OperandNameOrderKey])<=(769)))",
        [("OperationalMetricProjectionEvidence", "CK_OperationalMetricProjectionEvidence_Ordinal")] =
            "([EvidenceOrdinal]>=(0))",
        [("OperationalMetricProjectionEvidence", "CK_OperationalMetricProjectionEvidence_Kind")] =
            "([EvidenceKind]=(2) OR [EvidenceKind]=(1))",
        [("OperationalMetricProjectionEvidence", "CK_OperationalMetricProjectionEvidence_SubtypeShape")] =
            "([EvidenceKind]=(1) AND [ComponentKey] IS NOT NULL AND datalength([ComponentKey])>(0) AND [MetricDimension] IS NOT NULL AND ([MetricDimension]=(2) OR [MetricDimension]=(1) OR [MetricDimension]=(0)) AND [ComponentValue] IS NOT NULL AND datalength([ComponentValue])>(0) AND [ComponentUnit] IS NOT NULL AND datalength([ComponentUnit])>(0) AND [InputCount] IS NOT NULL AND ([InputCount]>=(1) AND [InputCount]<=(9223372036854775807.)) AND [FirstInputTimestamp] IS NOT NULL AND [LastInputTimestamp] IS NOT NULL AND datepart(tzoffset,[FirstInputTimestamp])=(0) AND datepart(tzoffset,[LastInputTimestamp])=(0) AND [LastInputTimestamp]>=[FirstInputTimestamp] AND [DependencyMetricKey] IS NULL AND [DependencyDefinitionVersion] IS NULL AND [DependencySnapshotCodecVersion] IS NULL AND [DependencySnapshotHash] IS NULL AND [DependencySnapshotBinary] IS NULL OR [EvidenceKind]=(2) AND [ComponentKey] IS NULL AND [MetricDimension] IS NULL AND [ComponentValue] IS NULL AND [ComponentUnit] IS NULL AND [InputCount] IS NULL AND [FirstInputTimestamp] IS NULL AND [LastInputTimestamp] IS NULL AND [DependencyMetricKey] IS NOT NULL AND datalength([DependencyMetricKey])>(0) AND [DependencyDefinitionVersion] IS NOT NULL AND datalength([DependencyDefinitionVersion])>(0) AND [DependencySnapshotCodecVersion] IS NOT NULL AND [DependencySnapshotCodecVersion]=(1) AND [DependencySnapshotHash] IS NOT NULL AND [DependencySnapshotBinary] IS NOT NULL AND (datalength([DependencySnapshotBinary])>=(1) AND datalength([DependencySnapshotBinary])<=(16777216)))",

        [("MachineShiftOccurrenceRoster", "CK_MachineShiftOccurrenceRoster_Site")] =
            "(datalength([ProductionDaySiteId])>(0) AND (datalength([ProductionDaySiteOrderKey])>=(1) AND datalength([ProductionDaySiteOrderKey])<=(769)))",
        [("MachineShiftOccurrenceRoster", "CK_MachineShiftOccurrenceRoster_ProductionLine")] =
            "(datalength([ProductionLineId])>(0))",
        [("MachineShiftOccurrenceRoster", "CK_MachineShiftOccurrenceRoster_Revision")] =
            "([Revision]>=(1) AND [Revision]<=(18446744073709551615.))",

        [("MachineShiftOccurrenceRosterOccurrence", "CK_MachineShiftOccurrenceRosterOccurrence_ShiftIdentity")] =
            "(datalength([ShiftScheduleAssignmentId])>(0) AND (datalength([ShiftScheduleAssignmentOrderKey])>=(1) AND datalength([ShiftScheduleAssignmentOrderKey])<=(769)) AND datalength([ShiftId])>(0) AND (datalength([ShiftOrderKey])>=(1) AND datalength([ShiftOrderKey])<=(769)))",
        [("MachineShiftOccurrenceRosterOccurrence", "CK_MachineShiftOccurrenceRosterOccurrence_Time")] =
            "(datepart(tzoffset,[ShiftStartsAtUtc])=(0) AND datepart(tzoffset,[ShiftEndsAtUtc])=(0) AND [ShiftEndsAtUtc]>[ShiftStartsAtUtc])",
    };
}
