import assert from "node:assert/strict";
import test from "node:test";

import {
  buildProductionDayQueryRequest,
  isProductionDaySelection,
} from "../src/application/production-day-reporting.ts";

const sources = [
  {
    machineId: "11111111-1111-1111-1111-111111111111",
    processorId: "operational-metrics-a",
    displayName: "Machine A",
  },
  {
    machineId: "22222222-2222-2222-2222-222222222222",
    processorId: "operational-metrics-b",
    displayName: "Machine B",
  },
];

test("production-day request uses the selected day and configured source identities only", () => {
  assert.deepEqual(buildProductionDayQueryRequest("2026-08-31", sources), {
    sources: [
      {
        machineId: "11111111-1111-1111-1111-111111111111",
        processorId: "operational-metrics-a",
      },
      {
        machineId: "22222222-2222-2222-2222-222222222222",
        processorId: "operational-metrics-b",
      },
    ],
    fromInclusive: "2026-08-31",
    toExclusive: "2026-09-01",
    metrics: null,
    context: null,
    statuses: null,
    order: "period-ascending",
    pageSize: 100,
    continuationToken: null,
  });
});

test("production-day request advances calendar boundaries without local-time interpretation", () => {
  assert.equal(buildProductionDayQueryRequest("2026-12-31", sources).toExclusive, "2027-01-01");
  assert.equal(buildProductionDayQueryRequest("2028-02-29", sources).toExclusive, "2028-03-01");
  assert.equal(buildProductionDayQueryRequest("9999-12-30", sources).toExclusive, "9999-12-31");
});

test("production-day selection accepts only valid queryable DateOnly calendar dates", () => {
  assert.equal(isProductionDaySelection("0000-01-01"), false);
  assert.equal(isProductionDaySelection("0001-01-01"), true);
  assert.equal(isProductionDaySelection("2026-08-31"), true);
  assert.equal(isProductionDaySelection("2028-02-29"), true);
  assert.equal(isProductionDaySelection("9999-12-30"), true);
  assert.equal(isProductionDaySelection("9999-12-31"), false);
  assert.equal(isProductionDaySelection("2026-02-29"), false);
  assert.equal(isProductionDaySelection("2026-8-31"), false);
  assert.equal(isProductionDaySelection("not-a-date"), false);
});

test("invalid production-day boundaries cannot construct a reporting request", () => {
  for (const productionDay of ["0000-01-01", "9999-12-31", "2026-02-29"]) {
    assert.throws(
      () => buildProductionDayQueryRequest(productionDay, sources),
      /valid queryable YYYY-MM-DD calendar date/,
    );
  }
});
