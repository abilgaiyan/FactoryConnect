"""Independent SQL SELECT name/type contract. No database access.
Provider metadata is preserved separately, never inferred from fixture values.
Expression nullability is captured, not invented; scalar/cardinality rules below
independently require the target/cut/summary fields to be present and non-NULL.
"""
from evidence_export import SETS
SERVER = r'DESKTOP-1PJK2OB\FCINVENTORY'
DATABASE = 'FactoryConnect_GG003_IMP1C_INVENTORY_20261020'
MACHINE = '677b030b-1ede-4a67-82ae-6a494791357f'
PROCESSOR = 'metric-aggregation:' + MACHINE

# Ordered columns transcribed from frozen SQL SELECTs and the uploaded Q1 schema.
def columns(spec):
    return [('ResultSet','nvarchar')] + [tuple(x.split(':')) for x in spec.split()]
CONTRACT = dict(zip(SETS, [
 columns('SqlServer:sql_variant DatabaseName:nvarchar OriginalLogin:nvarchar DatabaseUser:nvarchar is_read_only:bit CapturedAt:datetimeoffset'),
 columns('MetricAggregationProcessorRowId:bigint ProcessorKey:nvarchar MetricInputStreamRowId:bigint StreamKey:nvarchar MachineId:uniqueidentifier CommittedThroughMetricInputPosition:decimal'),
 columns('AdmittedContributionCount:bigint FirstAdmittedPosition:decimal LastAdmittedPosition:decimal StoredFactsThroughPositionDiagnosticOnly:bigint StoredButNotExactMembersDiagnosticOnly:bigint'),
 columns('ContributionPosition:decimal ContributionStream:bigint ContributionFactRow:bigint MachineId:uniqueidentifier FactId:nvarchar FactIdBinary:varbinary FactPosition:decimal MetricInputKey:nvarchar MetricValue:nvarchar Unit:nvarchar StartsAtUtc:datetimeoffset EndsAtUtc:datetimeoffset IsPlannedProductionTime:bit PlannedProductionScheduleAssignmentId:nvarchar SourceEligibilityIntervalId:nvarchar SourceContextualizedActivityIntervalId:nvarchar CompanyId:nvarchar SiteId:nvarchar ProductionLineId:nvarchar ProductionContextAssignmentId:nvarchar ProductionOrderId:nvarchar OperationId:nvarchar PartId:nvarchar OperatorId:nvarchar OccurrenceSiteId:nvarchar OccurrenceShiftScheduleAssignmentId:nvarchar OccurrenceShiftId:nvarchar OccurrenceStartsAtUtc:datetimeoffset OccurrenceEndsAtUtc:datetimeoffset ProductionDaySiteId:nvarchar ProductionBusinessDate:date'),
 columns('ProductionDaySiteId:nvarchar ProductionBusinessDate:date OccurrenceSiteId:nvarchar OccurrenceShiftScheduleAssignmentId:nvarchar OccurrenceShiftId:nvarchar OccurrenceStartsAtUtc:datetimeoffset OccurrenceEndsAtUtc:datetimeoffset MetricInputKey:nvarchar Unit:nvarchar IsPlannedProductionTime:bit AdmittedFactCount:bigint FirstPosition:decimal LastPosition:decimal'),
 columns('ContributionCount:bigint ReferencedFactAbsent:bigint ExactJoinMissing:bigint WrongContributionStream:bigint FactBindingMismatch:bigint WrongExistingFactMachine:bigint FactMachineUnverifiable:bigint NullEligibilityReferences:bigint NullContextReferences:bigint DuplicatePositionGroups:bigint DuplicateFactRowGroups:bigint'),
 columns('SqlServer:sql_variant DatabaseName:nvarchar is_read_only:bit')
]))
