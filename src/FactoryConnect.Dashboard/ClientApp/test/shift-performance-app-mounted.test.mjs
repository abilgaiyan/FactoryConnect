import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import React from "react";
import { createServer } from "vite";

import { mountInDom } from "./dom-test-harness.mjs";

const clientRoot = fileURLToPath(new URL("../", import.meta.url));
const vite = await createServer({
  root: clientRoot,
  server: { middlewareMode: true },
  appType: "custom",
  logLevel: "silent",
});
const { App } = await vite.ssrLoadModule("/src/App.tsx");
after(async () => vite.close());

const source = {
  machineId: "11111111-1111-1111-1111-111111111111",
  processorId: "operational-metrics",
  siteId: "factory-1",
  productionLineId: "line-1",
  displayName: "Machine 1",
  groupName: "Line 1",
  displayOrder: 10,
};

const expectedMetrics = [
  { metricKey: "Availability", version: "1.0" },
  { metricKey: "Utilization", version: "1.0" },
  { metricKey: "Performance", version: "1.0" },
  { metricKey: "Quality", version: "1.0" },
  { metricKey: "OEE", version: "1.0" },
];

function createRuntime({ sources = [source], queryProductionDayShiftMetrics } = {}) {
  const calls = {
    productionDayShift: [],
    productionDay: [],
    shift: [],
  };

  return {
    calls,
    runtime: {
      configuration: {
        reportingBasePath: "/",
        requestTimeoutMilliseconds: 30_000,
        sources,
      },
      reportingClient: {
        async queryProductionDayShiftMetrics(request, options) {
          calls.productionDayShift.push({ request, options });
          return queryProductionDayShiftMetrics
            ? queryProductionDayShiftMetrics(request, options)
            : { items: [], continuationToken: null };
        },
        async queryProductionDayMetrics(request) {
          calls.productionDay.push(request);
          return { items: [], continuationToken: null };
        },
        async queryShiftMetrics(request) {
          calls.shift.push(request);
          return { items: [], continuationToken: null };
        },
      },
      now: () => new Date("2026-09-03T00:00:00.000Z"),
    },
  };
}

function shiftInput(document) {
  return document.querySelector("#shift-production-day");
}

function selectedDayText(document) {
  return document.querySelector("time")?.textContent ?? null;
}

function assertExactShiftRequest(call, day) {
  assert.deepEqual(call.request.sources, [{
    machineId: source.machineId,
    processorId: source.processorId,
    siteId: source.siteId,
    businessDate: day,
  }]);
  assert.deepEqual(call.request.metrics, expectedMetrics);
  assert.deepEqual(call.request.context, {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    unpartitionedOnly: true,
  });
  assert.equal(call.request.pageSize, 200);
  assert.equal(call.request.continuationToken, null);
  assert.ok(call.options.signal instanceof AbortSignal);
  assert.equal("order" in call.request, false);
  assert.equal("startsAtOrAfterUtc" in call.request, false);
  assert.equal("startsBeforeUtc" in call.request, false);
}

function assertOnlyProductionDayShiftReporting(calls) {
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);
}

function assertConfiguredMachineWithZeroOccurrences(document) {
  assert.match(document.body.textContent, /Machine 1/);
  assert.match(document.body.textContent, /No authoritative shift occurrences returned\./i);
}

test("direct valid shift route queries the exact production-day shift identity and renders authoritative zero occurrences", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  assert.equal(selectedDayText(harness.document), "2026-09-02");
  assert.equal(shiftInput(harness.document).value, "2026-09-02");
  assert.equal(calls.productionDayShift.length, 1);
  assertExactShiftRequest(calls.productionDayShift[0], "2026-09-02");
  assertOnlyProductionDayShiftReporting(calls);
  assertConfiguredMachineWithZeroOccurrences(harness.document);
});

test("controlled day submission replaces route identity and queries exact day B", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  assert.equal(calls.productionDayShift.length, 1);
  await harness.changeInput(shiftInput(harness.document), "2026-09-03");
  await harness.submit(shiftInput(harness.document).form);

  assert.equal(harness.window.location.pathname, "/production-days/2026-09-03/shifts");
  assert.equal(selectedDayText(harness.document), "2026-09-03");
  assert.equal(shiftInput(harness.document).value, "2026-09-03");
  assert.equal(calls.productionDayShift.length, 2);
  assertExactShiftRequest(calls.productionDayShift[1], "2026-09-03");
  assertOnlyProductionDayShiftReporting(calls);
  assertConfiguredMachineWithZeroOccurrences(harness.document);
});

test("popstate from day A to day B aborts A and cannot publish A under B", async (t) => {
  let resolveA;
  const { runtime, calls } = createRuntime({
    queryProductionDayShiftMetrics(request) {
      if (request.sources[0].businessDate === "2026-09-02") {
        return new Promise((resolve) => { resolveA = resolve; });
      }
      return Promise.resolve({ items: [], continuationToken: null });
    },
  });
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  assert.equal(calls.productionDayShift.length, 1);
  const signalA = calls.productionDayShift[0].options.signal;
  await harness.popstate("/production-days/2026-09-03/shifts");

  assert.equal(signalA.aborted, true);
  assert.equal(calls.productionDayShift.length, 2);
  assertExactShiftRequest(calls.productionDayShift[1], "2026-09-03");
  assert.equal(selectedDayText(harness.document), "2026-09-03");
  assertConfiguredMachineWithZeroOccurrences(harness.document);

  resolveA({ items: [], continuationToken: null });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(selectedDayText(harness.document), "2026-09-03");
  assertConfiguredMachineWithZeroOccurrences(harness.document);
  assertOnlyProductionDayShiftReporting(calls);
});

test("malformed shift route day is visible and invalid submission does not navigate or query", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/not-a-date/shifts");
  t.after(() => harness.dispose());

  assert.match(harness.document.querySelector("[role=alert]").textContent, /not a valid calendar date/i);
  assert.equal(shiftInput(harness.document).value, "");
  const before = harness.window.location.pathname;
  await harness.submit(shiftInput(harness.document).form);
  assert.equal(harness.window.location.pathname, before);
  assert.equal(calls.productionDayShift.length, 0);
  assertOnlyProductionDayShiftReporting(calls);
});

test("production-day detail shift link starts exact selected-day shift reporting without legacy shift queries", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02");
  t.after(() => harness.dispose());

  const link = [...harness.document.querySelectorAll("a")].find((anchor) => anchor.textContent === "Shift performance");
  assert.ok(link);
  assert.equal(link.getAttribute("href"), "/production-days/2026-09-02/shifts");
  assert.equal(calls.productionDayShift.length, 0);
  assert.equal(calls.shift.length, 0);

  await harness.click(link);

  assert.equal(harness.window.location.pathname, "/production-days/2026-09-02/shifts");
  assert.equal(selectedDayText(harness.document), "2026-09-02");
  assert.equal(calls.productionDayShift.length, 1);
  assertExactShiftRequest(calls.productionDayShift[0], "2026-09-02");
  assert.equal(calls.shift.length, 0);
  assertConfiguredMachineWithZeroOccurrences(harness.document);
});

test("configured zero-source factory completes authoritatively without any reporting operation", async (t) => {
  const { runtime, calls } = createRuntime({ sources: [] });
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  assert.equal(calls.productionDayShift.length, 0);
  assertOnlyProductionDayShiftReporting(calls);
  assert.match(harness.document.body.textContent, /No configured machines\./i);
});
