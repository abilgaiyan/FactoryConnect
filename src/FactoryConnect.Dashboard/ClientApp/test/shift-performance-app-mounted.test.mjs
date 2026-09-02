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

function createRuntime() {
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
        sources: [source],
      },
      reportingClient: {
        async queryProductionDayShiftMetrics(request) {
          calls.productionDayShift.push(request);
          return { items: [], continuationToken: null };
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

test("direct shift route selects the exact day without issuing reporting requests", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  assert.equal(selectedDayText(harness.document), "2026-09-02");
  assert.equal(shiftInput(harness.document).value, "2026-09-02");
  assert.equal(calls.productionDayShift.length, 0);
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);
  assert.doesNotMatch(harness.container.textContent, /availability|utilization|performance|quality|oee|current state/i);
});

test("controlled shift day submission navigates and route identity owns the remounted input", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  await harness.changeInput(shiftInput(harness.document), "2026-09-03");
  assert.equal(shiftInput(harness.document).value, "2026-09-03");
  await harness.submit(shiftInput(harness.document).form);

  assert.equal(harness.window.location.pathname, "/production-days/2026-09-03/shifts");
  assert.equal(selectedDayText(harness.document), "2026-09-03");
  assert.equal(shiftInput(harness.document).value, "2026-09-03");
  assert.equal(calls.productionDayShift.length, 0);
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);
});

test("popstate from day A to day B discards stale local selector state", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02/shifts");
  t.after(() => harness.dispose());

  await harness.changeInput(shiftInput(harness.document), "2026-09-04");
  assert.equal(shiftInput(harness.document).value, "2026-09-04");

  await harness.popstate("/production-days/2026-09-03/shifts");

  assert.equal(selectedDayText(harness.document), "2026-09-03");
  assert.equal(shiftInput(harness.document).value, "2026-09-03");
  assert.equal(calls.productionDayShift.length, 0);
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);
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
  assert.equal(calls.productionDay.length, 0);
  assert.equal(calls.shift.length, 0);
});

test("production-day detail exposes and routes through the exact shift-performance link without a shift request", async (t) => {
  const { runtime, calls } = createRuntime();
  const harness = await mountInDom(React.createElement(App, { runtime }), "http://factory-dashboard/production-days/2026-09-02");
  t.after(() => harness.dispose());

  const link = [...harness.document.querySelectorAll("a")].find((anchor) => anchor.textContent === "Shift performance");
  assert.ok(link);
  assert.equal(link.getAttribute("href"), "/production-days/2026-09-02/shifts");

  await harness.click(link);

  assert.equal(harness.window.location.pathname, "/production-days/2026-09-02/shifts");
  assert.equal(selectedDayText(harness.document), "2026-09-02");
  assert.equal(shiftInput(harness.document).value, "2026-09-02");
  assert.equal(calls.productionDayShift.length, 0);
  assert.equal(calls.shift.length, 0);
});
