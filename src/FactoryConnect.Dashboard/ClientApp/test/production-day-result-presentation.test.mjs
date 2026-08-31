import assert from "node:assert/strict";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ProductionDayMetricResults } from "../src/application/ProductionDayMetricResults.tsx";

const configuredMachineId = "11111111-1111-1111-1111-111111111111";
const unknownMachineId = "22222222-2222-2222-2222-222222222222";

function metric(overrides) {
  return {
    scope: "production-day",
    processorId: "operational-metrics",
    machineId: configuredMachineId,
    shift: null,
    productionDay: { siteId: "site-1", businessDate: "2026-08-30" },
    context: {
      productionOrderId: "order-7",
      operationId: "operation-3",
      partId: "part-9",
      operatorId: "operator-4",
    },
    metricKey: "Availability",
    definitionVersion: "1.0",
    status: "calculated",
    value: 0,
    unit: "ratio",
    reasonCode: null,
    reasonOperandName: null,
    sourceRevision: {
      processorId: "operational-metrics",
      machineId: configuredMachineId,
      streamKey: "stream-a",
      position: "18446744073709551615",
    },
    ...overrides,
  };
}

test("production-day results preserve authoritative states, context, revision, and unknown source identity", () => {
  const page = {
    items: [
      metric({}),
      metric({
        machineId: unknownMachineId,
        metricKey: "Performance",
        status: "unavailable",
        value: null,
        reasonCode: "missing-reference-time",
        reasonOperandName: "ProductionReferenceTime",
        sourceRevision: {
          processorId: "operational-metrics",
          machineId: unknownMachineId,
          streamKey: "stream-b",
          position: 42,
        },
      }),
      metric({
        metricKey: "Quality",
        status: "insufficient-evidence",
        value: null,
        reasonCode: "missing-good-count",
        reasonOperandName: null,
        sourceRevision: {
          processorId: "operational-metrics",
          machineId: configuredMachineId,
          streamKey: "stream-c",
          position: 43,
        },
      }),
    ],
    continuationToken: "opaque-next-token",
  };

  const html = renderToStaticMarkup(React.createElement(ProductionDayMetricResults, {
    page,
    sources: [{
      machineId: configuredMachineId,
      processorId: "operational-metrics",
      displayName: "Machine 1",
    }],
  }));

  assert.match(html, />0<\/td>/);
  assert.match(html, /calculated/);
  assert.match(html, /unavailable/);
  assert.match(html, /insufficient-evidence/);
  assert.match(html, /missing-reference-time \(ProductionReferenceTime\)/);
  assert.match(html, /missing-good-count/);
  assert.match(html, /order-7/);
  assert.match(html, /operation-3/);
  assert.match(html, /part-9/);
  assert.match(html, /operator-4/);
  assert.match(html, /stream-a/);
  assert.match(html, /18446744073709551615/);
  assert.match(html, /Machine 1/);
  assert.match(html, /Unconfigured source/);
  assert.match(html, new RegExp(unknownMachineId));
  assert.match(html, /Additional reporting results are available\./);
});
