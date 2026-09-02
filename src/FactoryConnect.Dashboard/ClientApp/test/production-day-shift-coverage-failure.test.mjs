import assert from "node:assert/strict";
import test from "node:test";

import { createReportingClient } from "../src/api/reporting/reporting-client.ts";
import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingHttpFailure,
  ReportingProtocolFailure,
} from "../src/api/reporting/reporting-response-failures.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

const machineId = "11111111-1111-1111-1111-111111111111";
const siteId = "site-a";
const businessDate = "2026-09-02";
const problemType = "urn:factoryconnect:problem:reporting:production-day-shift-roster-coverage-required";
const problemCode = "production-day-shift-roster-coverage-required";

function problem(overrides = {}) {
  return {
    type: problemType,
    title: "Production-day shift roster coverage required",
    status: 409,
    code: problemCode,
    machineId,
    siteId,
    businessDate,
    ...overrides,
  };
}

function clientFor(responseFactory) {
  return createReportingClient({
    baseAddress: "http://dashboard.test/",
    timeoutMilliseconds: 1000,
    fetch: async () => responseFactory(),
  });
}

function conflict(body, contentType = "application/problem+json") {
  return new Response(JSON.stringify(body), { status: 409, headers: { "Content-Type": contentType } });
}

test("production-day shift 409 becomes typed roster coverage failure with authoritative identity", async () => {
  const client = clientFor(() => conflict(problem()));
  await assert.rejects(
    client.queryProductionDayShiftMetrics({}),
    failure => failure instanceof ProductionDayShiftRosterCoverageRequiredFailure
      && failure.machineId === machineId
      && failure.siteId === siteId
      && failure.businessDate === businessDate
      && failure.problemDetails.type === problemType,
  );
});

test("unknown 409 remains generic HTTP failure and retains Problem Details", async () => {
  const client = clientFor(() => conflict({ type: "urn:factoryconnect:problem:reporting:future-conflict", status: 409 }));
  await assert.rejects(
    client.queryProductionDayShiftMetrics({}),
    failure => failure instanceof ReportingHttpFailure
      && failure.status === 409
      && failure.problemDetails?.type === "urn:factoryconnect:problem:reporting:future-conflict",
  );
});

test("malformed known coverage identity is a protocol failure rather than a partial typed failure", async () => {
  for (const malformed of [
    problem({ code: "wrong" }),
    problem({ machineId: "" }),
    problem({ siteId: " " }),
    problem({ businessDate: "02-09-2026" }),
  ]) {
    const client = clientFor(() => conflict(malformed));
    await assert.rejects(client.queryProductionDayShiftMetrics({}), ReportingProtocolFailure);
  }
});

test("coverage conflict requires Problem Details media type", async () => {
  const client = clientFor(() => conflict(problem(), "application/json"));
  await assert.rejects(client.queryProductionDayShiftMetrics({}), ReportingProtocolFailure);
});

test("query lifecycle publishes roster coverage separately from generic failure", async () => {
  const failure = new ProductionDayShiftRosterCoverageRequiredFailure(problem(), { machineId, siteId, businessDate });
  const controller = createQueryLifecycleController({
    query: async () => { throw failure; },
    isEmpty: () => false,
  });

  assert.deepEqual(await controller.execute(), {
    kind: "coverageRequired",
    details: { machineId, siteId, businessDate },
  });
});
