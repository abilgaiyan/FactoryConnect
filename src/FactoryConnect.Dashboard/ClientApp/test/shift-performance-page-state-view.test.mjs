import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ShiftPerformancePageStateView } from "../src/presentation/ShiftPerformancePageStateView.tsx";

const day = "2026-09-03";

function overview(value = "0.37") {
  return {
    productionDay: day,
    groups: [{
      groupName: "Line A",
      machines: [{
        machineId: "M1",
        processorId: "P1",
        displayName: "Machine 1",
        productionLineId: "line-a",
        shifts: [{
          productionLineId: "line-a",
          sourceRevision: null,
          shift: {
            siteId: "site-a",
            shiftScheduleAssignmentId: "assignment-a",
            shiftId: "Shift A",
            startsAtUtc: "2026-09-03T00:00:00Z",
            endsAtUtc: "2026-09-03T08:00:00Z",
          },
          availability: { kind: "missing" },
          utilization: { kind: "missing" },
          performance: { kind: "missing" },
          quality: { kind: "missing" },
          oee: {
            kind: "reported",
            status: "calculated",
            value,
            unit: "Ratio",
            reasonCode: null,
            reasonOperandName: null,
          },
        }],
      }],
    }],
  };
}

function render(state) {
  return renderToStaticMarkup(createElement(ShiftPerformancePageStateView, { state }));
}

test("loading renders status without an overview", () => {
  const html = render({ kind: "loading", productionDay: day });
  assert.match(html, /role="status"/);
  assert.match(html, /Loading shift performance/);
  assert.doesNotMatch(html, /Machine 1/);
});

test("ordinary success delegates the exact supplied overview without refresh presentation", () => {
  const html = render({ kind: "success", productionDay: day, overview: overview("0.37"), isRefreshing: false });
  assert.match(html, /Machine 1/);
  assert.match(html, /Shift A/);
  assert.match(html, /37%/);
  assert.doesNotMatch(html, /Refreshing shift performance/);
});

test("refreshing success renders the same overview plus status only", () => {
  const supplied = overview("0.37");
  const html = render({ kind: "success", productionDay: day, overview: supplied, isRefreshing: true });
  assert.match(html, /Machine 1/);
  assert.match(html, /Shift A/);
  assert.match(html, /37%/);
  assert.match(html, /role="status"/);
  assert.match(html, /Refreshing shift performance/);
  assert.equal(supplied.groups[0].machines[0].shifts[0].oee.value, "0.37");
});

test("invalid request renders the already-classified message as an alert", () => {
  const html = render({ kind: "invalid-request", productionDay: day, message: "classified invalid request" });
  assert.match(html, /role="alert"/);
  assert.match(html, /classified invalid request/);
});

test("roster coverage alert preserves exact machine site and business-date identity", () => {
  const html = render({
    kind: "roster-coverage-required",
    productionDay: day,
    machineId: "Machine/RAW-01",
    siteId: "Site RAW A",
    businessDate: "2026-09-03",
  });
  assert.match(html, /role="alert"/);
  assert.match(html, /Machine\/RAW-01/);
  assert.match(html, /Site RAW A/);
  assert.match(html, /2026-09-03/);
});

test("transport failure renders the already-classified reporting message as an alert", () => {
  const html = render({ kind: "transport-failure", productionDay: day, message: "classified reporting failure" });
  assert.match(html, /role="alert"/);
  assert.match(html, /classified reporting failure/);
});

test("presentation contract failure renders the already-classified presentation message as an alert", () => {
  const html = render({ kind: "presentation-contract-failure", productionDay: day, message: "classified presentation failure" });
  assert.match(html, /role="alert"/);
  assert.match(html, /classified presentation failure/);
});
