import assert from "node:assert/strict";
import test from "node:test";

import type {
  PresentedMetric,
  ShiftPerformanceShift,
} from "../src/presentation/shift-performance-model.ts";
import {
  ShiftPresentationContractFailure,
} from "../src/presentation/shift-performance-model.ts";

const shift = {
  siteId: "site-1",
  shiftScheduleAssignmentId: "assignment-1",
  shiftId: "shift-a",
  startsAtUtc: "2026-09-01T22:00:00Z",
  endsAtUtc: "2026-09-02T06:00:00Z",
};

test("presentation metric preserves authoritative decimal representation verbatim", () => {
  const metric: PresentedMetric = {
    state: "calculated",
    value: "0.37000000000000000000",
    unit: "Ratio",
  };

  assert.equal(metric.value, "0.37000000000000000000");
});

test("missing presentation metric carries no manufactured value", () => {
  const metric: PresentedMetric = { state: "missing" };

  assert.deepEqual(metric, { state: "missing" });
  assert.equal("value" in metric, false);
  assert.equal("unit" in metric, false);
});

test("shift presentation preserves null source revision distinctly from a revision", () => {
  const withoutEvidence: ShiftPerformanceShift = {
    shift,
    productionLineId: "line-1",
    sourceRevision: null,
    availability: { state: "missing" },
    utilization: { state: "missing" },
    performance: { state: "missing" },
    quality: { state: "missing" },
    oee: { state: "missing" },
  };

  const withEvidence: ShiftPerformanceShift = {
    ...withoutEvidence,
    sourceRevision: 42,
  };

  assert.equal(withoutEvidence.sourceRevision, null);
  assert.equal(withEvidence.sourceRevision, 42);
});

test("presentation contract failure retains its typed reason", () => {
  const failure = new ShiftPresentationContractFailure(
    "unexpected-production-line",
    "Authoritative production line does not match configured source.",
  );

  assert.equal(failure.name, "ShiftPresentationContractFailure");
  assert.equal(failure.reason, "unexpected-production-line");
});
