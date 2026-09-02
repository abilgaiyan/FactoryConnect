import assert from "node:assert/strict";
import test from "node:test";

import { projectShiftPerformanceOverview } from "../src/presentation/shift-performance-projector.ts";

const day = "2026-09-02";

function source(machineId, processorId, groupName, displayOrder, productionLineId = "line-a") {
  return {
    machineId,
    processorId,
    siteId: "site-a",
    productionLineId,
    displayName: `Machine ${machineId}`,
    groupName,
    displayOrder,
  };
}

function metric(metricKey, status = "calculated", overrides = {}) {
  const calculated = status === "calculated";
  return {
    metricKey,
    definitionVersion: "1.0",
    status,
    value: calculated ? "0.8" : null,
    unit: "Ratio",
    reasonCode: calculated ? null : "missing-input",
    reasonOperandName: calculated ? null : "ActualProductionTime",
    ...overrides,
  };
}

function report(sourceValue, shiftId, startsAtUtc, metrics = [], sourceRevision = null) {
  return {
    processorId: sourceValue.processorId,
    machineId: sourceValue.machineId,
    productionDay: { siteId: sourceValue.siteId, businessDate: day },
    productionLineId: sourceValue.productionLineId,
    shift: {
      siteId: sourceValue.siteId,
      shiftScheduleAssignmentId: "assignment-a",
      shiftId,
      startsAtUtc,
      endsAtUtc: startsAtUtc.replace(/T(00|08|16):/, (_, hour) => `T${String(Number(hour) + 8).padStart(2, "0")}:`),
    },
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision,
    metrics,
  };
}

test("projects configured groups and machines in configured first-occurrence order", () => {
  const m1 = source("M1", "P1", "Line A", 10, "line-a");
  const m2 = source("M2", "P2", "Line A", 20, "line-a");
  const m3 = source("M3", "P3", "Line B", 30, "line-b");
  const result = {
    items: [
      report(m1, "Shift A", "2026-09-02T00:00:00Z"),
      report(m3, "Shift A", "2026-09-02T00:00:00Z"),
      report(m1, "Shift B", "2026-09-02T08:00:00Z"),
    ],
  };

  const overview = projectShiftPerformanceOverview(day, [m1, m2, m3], result);

  assert.deepEqual(overview.groups.map(group => group.groupName), ["Line A", "Line B"]);
  assert.deepEqual(overview.groups[0].machines.map(machine => machine.machineId), ["M1", "M2"]);
  assert.deepEqual(overview.groups[1].machines.map(machine => machine.machineId), ["M3"]);
  assert.deepEqual(overview.groups[0].machines[0].shifts.map(shift => shift.shift.shiftId), ["Shift A", "Shift B"]);
  assert.deepEqual(overview.groups[0].machines[1].shifts, []);
});

test("does not independently sort authoritative per-source occurrence order", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const first = report(m1, "Shift B", "2026-09-02T08:00:00Z");
  const second = report(m1, "Shift A", "2026-09-02T00:00:00Z");
  const overview = projectShiftPerformanceOverview(day, [m1], { items: [first, second] });
  assert.deepEqual(overview.groups[0].machines[0].shifts.map(shift => shift.shift.shiftId), ["Shift B", "Shift A"]);
});

test("preserves exact shift descriptor production line and non-null source revision", () => {
  const m1 = source("M1", "projection-shifts", "Line A", 0);
  const revision = { machineId: "M1", processorId: "aggregation", streamKey: "metric-inputs", position: 73 };
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z", [], revision);
  const overview = projectShiftPerformanceOverview(day, [m1], { items: [item] });
  const projected = overview.groups[0].machines[0].shifts[0];

  assert.equal(projected.shift, item.shift);
  assert.equal(projected.productionLineId, item.productionLineId);
  assert.equal(projected.sourceRevision, revision);
});

test("preserves null revision distinctly while manufacturing five missing slots", () => {
  const m1 = source("M1", "P1", null, 0);
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z");
  const projected = projectShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];

  assert.equal(projected.sourceRevision, null);
  for (const [property, metricKey] of [["availability", "Availability"], ["utilization", "Utilization"], ["performance", "Performance"], ["quality", "Quality"], ["oee", "OEE"]]) {
    assert.deepEqual(projected[property], { metricKey, version: "1.0", state: "missing" });
  }
});

test("projects partial metric evidence and manufactures only absent requested slots", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z", [
    metric("Availability", "calculated", { value: "0.80" }),
    metric("Performance", "unavailable", { reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" }),
    metric("Quality", "insufficient-evidence", { reasonCode: "missing-counts", reasonOperandName: null }),
  ]);
  const projected = projectShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];

  assert.deepEqual(projected.availability, { metricKey: "Availability", version: "1.0", state: "calculated", value: "0.80", unit: "Ratio" });
  assert.deepEqual(projected.performance, { metricKey: "Performance", version: "1.0", state: "unavailable", reasonCode: "missing-reference-time", reasonOperandName: "ReferenceTime" });
  assert.deepEqual(projected.quality, { metricKey: "Quality", version: "1.0", state: "insufficient-evidence", reasonCode: "missing-counts", reasonOperandName: null });
  assert.deepEqual(projected.utilization, { metricKey: "Utilization", version: "1.0", state: "missing" });
  assert.deepEqual(projected.oee, { metricKey: "OEE", version: "1.0", state: "missing" });
});

test("preserves authoritative OEE instead of recalculating it", () => {
  const m1 = source("M1", "P1", "Line A", 0);
  const item = report(m1, "Shift A", "2026-09-02T00:00:00Z", [
    metric("Availability", "calculated", { value: "0.80" }),
    metric("Performance", "calculated", { value: "0.50" }),
    metric("Quality", "calculated", { value: "0.90" }),
    metric("OEE", "calculated", { value: "0.37" }),
  ]);
  const projected = projectShiftPerformanceOverview(day, [m1], { items: [item] }).groups[0].machines[0].shifts[0];
  assert.equal(projected.oee.value, "0.37");
});

test("supports an empty configured factory population", () => {
  assert.deepEqual(projectShiftPerformanceOverview(day, [], { items: [] }), { productionDay: day, groups: [] });
});
