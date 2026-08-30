import type { components, paths } from "./generated/reporting-contract";

type Assert<T extends true> = T;
type HasKey<T, K extends PropertyKey> = K extends keyof T ? true : false;
type Equal<A, B> =
  (<T>() => T extends A ? 1 : 2) extends
  (<T>() => T extends B ? 1 : 2)
    ? (<T>() => T extends B ? 1 : 2) extends
        (<T>() => T extends A ? 1 : 2)
      ? true
      : false
    : false;

type Schemas = components["schemas"];
type ReportingSource = Schemas["ReportingSourceRequest"];
type MetricDefinition = Schemas["OperationalMetricDefinitionRequest"];
type Context = Schemas["OperationalMetricContextRequest"];
type ShiftQuery = Schemas["ShiftOperationalMetricQueryRequest"];
type ProductionDayQuery = Schemas["ProductionDayOperationalMetricQueryRequest"];
type MetricItem = Schemas["OperationalMetricItemResponse"];
type MetricPage = Schemas["OperationalMetricPageResponse"];
type SourceRevision = Schemas["MetricSourceRevisionResponse"];

type ExpectedStatuses =
  | "calculated"
  | "unavailable"
  | "insufficient-evidence";

type ShiftPathExists = Assert<
  HasKey<paths, "/api/reporting/v1/operational-metrics/shifts/query">
>;
type ProductionDayPathExists = Assert<
  HasKey<paths, "/api/reporting/v1/operational-metrics/production-days/query">
>;

type SourceHasMachineId = Assert<HasKey<ReportingSource, "machineId">>;
type SourceHasProcessorId = Assert<HasKey<ReportingSource, "processorId">>;
type DefinitionHasMetricKey = Assert<HasKey<MetricDefinition, "metricKey">>;
type DefinitionHasVersion = Assert<HasKey<MetricDefinition, "version">>;

type ContextHasProductionOrderId = Assert<HasKey<Context, "productionOrderId">>;
type ContextHasOperationId = Assert<HasKey<Context, "operationId">>;
type ContextHasPartId = Assert<HasKey<Context, "partId">>;
type ContextHasOperatorId = Assert<HasKey<Context, "operatorId">>;

type ShiftStatuses = NonNullable<ShiftQuery["statuses"]>[number];
type ProductionDayStatuses = NonNullable<ProductionDayQuery["statuses"]>[number];
type ShiftStatusVocabularyIsExact = Assert<Equal<ShiftStatuses, ExpectedStatuses>>;
type ProductionDayStatusVocabularyIsExact = Assert<
  Equal<ProductionDayStatuses, ExpectedStatuses>
>;

type MetricItemHasMetricKey = Assert<HasKey<MetricItem, "metricKey">>;
type MetricItemHasDefinitionVersion = Assert<HasKey<MetricItem, "definitionVersion">>;
type MetricItemHasReasonCode = Assert<HasKey<MetricItem, "reasonCode">>;
type MetricItemHasReasonOperandName = Assert<HasKey<MetricItem, "reasonOperandName">>;
type MetricItemHasSourceRevision = Assert<HasKey<MetricItem, "sourceRevision">>;
type MetricValueRemainsNullable = Assert<null extends MetricItem["value"] ? true : false>;
type MetricStatusVocabularyIsExact = Assert<Equal<MetricItem["status"], ExpectedStatuses>>;

type SourceRevisionHasProcessorId = Assert<HasKey<SourceRevision, "processorId">>;
type SourceRevisionHasMachineId = Assert<HasKey<SourceRevision, "machineId">>;
type SourceRevisionHasStreamKey = Assert<HasKey<SourceRevision, "streamKey">>;
type SourceRevisionHasPosition = Assert<HasKey<SourceRevision, "position">>;

type PageHasContinuationToken = Assert<HasKey<MetricPage, "continuationToken">>;
type ContinuationTokenRemainsNullable = Assert<
  null extends MetricPage["continuationToken"] ? true : false
>;

export type ReportingContractConformance = {
  shiftPath: ShiftPathExists;
  productionDayPath: ProductionDayPathExists;
  sourceMachineId: SourceHasMachineId;
  sourceProcessorId: SourceHasProcessorId;
  metricKey: DefinitionHasMetricKey & MetricItemHasMetricKey;
  definitionVersion: DefinitionHasVersion & MetricItemHasDefinitionVersion;
  productionOrderId: ContextHasProductionOrderId;
  operationId: ContextHasOperationId;
  partId: ContextHasPartId;
  operatorId: ContextHasOperatorId;
  shiftStatuses: ShiftStatusVocabularyIsExact;
  productionDayStatuses: ProductionDayStatusVocabularyIsExact;
  nullableValue: MetricValueRemainsNullable;
  exactStatuses: MetricStatusVocabularyIsExact;
  reasonCode: MetricItemHasReasonCode;
  reasonOperandName: MetricItemHasReasonOperandName;
  sourceRevision:
    & MetricItemHasSourceRevision
    & SourceRevisionHasProcessorId
    & SourceRevisionHasMachineId
    & SourceRevisionHasStreamKey
    & SourceRevisionHasPosition;
  continuationToken: PageHasContinuationToken & ContinuationTokenRemainsNullable;
};
