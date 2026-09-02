import type { ProductionDayShiftPage } from "../api/reporting/index.ts";
import type { AuthoritativeProductionDayShiftResult } from "../application/production-day-shift-reporting.ts";
import type { DashboardRuntimeSource } from "../application/runtime-configuration.ts";
import { ShiftPresentationContractFailure } from "./shift-performance-model.ts";

export interface ValidatedProductionDayShiftResult {
  readonly items: ProductionDayShiftPage["items"];
}

type ShiftReport = ProductionDayShiftPage["items"][number];
type ShiftMetric = ShiftReport["metrics"][number];

const allowedMetrics = new Set([
  "Availability\u00001.0",
  "Utilization\u00001.0",
  "Performance\u00001.0",
  "Quality\u00001.0",
  "OEE\u00001.0",
]);

export function validateProductionDayShiftAuthority(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  result: AuthoritativeProductionDayShiftResult,
): ValidatedProductionDayShiftResult {
  const configuredSources = new Map(
    sources.map((source) => [sourceKey(source.machineId, source.processorId), source] as const),
  );
  const seenReports = new Set<string>();
  const occurrenceDescriptors = new Map<string, string>();
  const previousBySource = new Map<string, ShiftReport>();

  for (const report of result.items) {
    const configured = configuredSources.get(sourceKey(report.machineId, report.processorId));
    if (configured === undefined) {
      fail("unexpected-source", "Shift report belongs to a source outside the configured factory population.");
    }

    if (report.productionDay.businessDate !== productionDay) {
      fail("unexpected-production-day", "Shift report belongs to a production day other than the selected production day.");
    }

    if (report.productionDay.siteId !== configured.siteId
      || report.shift.siteId !== configured.siteId
      || report.shift.siteId !== report.productionDay.siteId) {
      fail("unexpected-site", "Shift report site identity does not match the configured source and production day.");
    }

    if (report.productionLineId !== configured.productionLineId) {
      fail("unexpected-production-line", "Shift report production line does not match the configured scheduling lineage.");
    }

    if (!isCanonicalUnpartitionedContext(report.context)) {
      fail("unexpected-context", "Shift report context is not the canonical unpartitioned context requested by the overview.");
    }

    if (report.sourceRevision !== null
      && (report.sourceRevision.machineId !== report.machineId
        || report.sourceRevision.processorId !== report.processorId)) {
      fail("unexpected-source-revision", "Shift report source revision belongs to a different reporting source.");
    }

    if (!(report.shift.startsAtUtc < report.shift.endsAtUtc)) {
      fail("inconsistent-occurrence-descriptor", "Shift occurrence must start before it ends.");
    }

    const occurrenceKey = shiftOccurrenceKey(report);
    const descriptor = shiftDescriptor(report);
    const priorDescriptor = occurrenceDescriptors.get(occurrenceKey);
    if (priorDescriptor !== undefined && priorDescriptor !== descriptor) {
      fail("inconsistent-occurrence-descriptor", "The same shift occurrence identity has conflicting authoritative descriptors.");
    }
    occurrenceDescriptors.set(occurrenceKey, descriptor);

    const reportKey = `${sourceKey(report.machineId, report.processorId)}\u0000${report.productionDay.siteId}\u0000${report.productionDay.businessDate}\u0000${descriptor}`;
    if (seenReports.has(reportKey)) {
      fail("duplicate-occurrence", "The authoritative result contains a duplicate shift occurrence report.");
    }
    seenReports.add(reportKey);

    const source = sourceKey(report.machineId, report.processorId);
    const previous = previousBySource.get(source);
    if (previous !== undefined && compareOccurrence(previous, report) >= 0) {
      fail("out-of-order-occurrence", "Shift reports are not in strictly increasing authoritative occurrence order.");
    }
    previousBySource.set(source, report);

    validateMetrics(report.metrics);
  }

  return { items: result.items };
}

function validateMetrics(metrics: readonly ShiftMetric[]): void {
  const seen = new Set<string>();
  for (const metric of metrics) {
    const identity = `${metric.metricKey}\u0000${metric.definitionVersion}`;
    if (!allowedMetrics.has(identity)) {
      fail("unexpected-metric", "Shift report contains a metric outside the exact FC-029.3 overview vocabulary.");
    }
    if (seen.has(identity)) {
      fail("duplicate-metric", "Shift report contains a duplicate exact metric definition identity.");
    }
    seen.add(identity);

    if (metric.status === "calculated") {
      if (metric.value === null || metric.unit.length === 0) {
        fail("malformed-metric-state", "Calculated shift metrics require a value and non-empty unit.");
      }
    } else if (metric.value !== null) {
      fail("malformed-metric-state", "Non-calculated shift metrics must not carry a value.");
    }
  }
}

function isCanonicalUnpartitionedContext(context: ShiftReport["context"]): boolean {
  return context.productionOrderId === null
    && context.operationId === null
    && context.partId === null
    && context.operatorId === null;
}

function sourceKey(machineId: string, processorId: string): string {
  return `${machineId}\u0000${processorId}`;
}

function shiftOccurrenceKey(report: ShiftReport): string {
  return `${report.machineId}\u0000${report.productionLineId}\u0000${report.shift.shiftScheduleAssignmentId}\u0000${report.shift.shiftId}`;
}

function shiftDescriptor(report: ShiftReport): string {
  const shift = report.shift;
  return `${shift.siteId}\u0000${shift.shiftScheduleAssignmentId}\u0000${shift.shiftId}\u0000${shift.startsAtUtc}\u0000${shift.endsAtUtc}`;
}

function compareOccurrence(left: ShiftReport, right: ShiftReport): number {
  return compareText(left.shift.startsAtUtc, right.shift.startsAtUtc)
    || compareText(left.shift.endsAtUtc, right.shift.endsAtUtc)
    || compareText(left.shift.siteId, right.shift.siteId)
    || compareText(left.shift.shiftScheduleAssignmentId, right.shift.shiftScheduleAssignmentId)
    || compareText(left.shift.shiftId, right.shift.shiftId);
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function fail(
  reason: ConstructorParameters<typeof ShiftPresentationContractFailure>[0],
  message: string,
): never {
  throw new ShiftPresentationContractFailure(reason, message);
}
