import assert from "node:assert/strict";
import test from "node:test";

import { ReportingNetworkFailure } from "../src/api/reporting/index.ts";
import { createDashboardApplicationRuntime } from "../src/application/application-runtime.ts";
import { loadDashboardRuntimeConfiguration } from "../src/application/runtime-configuration.ts";
import { presentQueryState } from "../src/query/query-state-presentation.ts";

const runtimeConfiguration = {
  reportingBasePath: "/",
  requestTimeoutMilliseconds: 30_000,
  sources: [
    {
      machineId: "11111111-1111-1111-1111-111111111111",
      processorId: "operational-metrics",
      displayName: "Machine 1",
    },
  ],
};

test("runtime configuration loads only from the dashboard same-origin endpoint", async () => {
  const calls = [];
  const configuration = await loadDashboardRuntimeConfiguration(async (input, init) => {
    calls.push({ input, init });
    return new Response(JSON.stringify(runtimeConfiguration), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  });

  assert.deepEqual(configuration, runtimeConfiguration);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].input, "/dashboard/config");
  assert.equal(calls[0].init.method, "GET");
});

test("application runtime constructs one reporting client from current origin and runtime config", async () => {
  let fetchCount = 0;
  const fetchImplementation = async () => {
    fetchCount += 1;
    return new Response(JSON.stringify(runtimeConfiguration), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };

  const runtime = await createDashboardApplicationRuntime(
    "http://factory-dashboard:5090",
    fetchImplementation,
  );

  assert.equal(fetchCount, 1);
  assert.deepEqual(runtime.configuration, runtimeConfiguration);
  assert.equal(typeof runtime.reportingClient.queryShiftMetrics, "function");
  assert.equal(typeof runtime.reportingClient.queryProductionDayMetrics, "function");
});

test("malformed runtime configuration is rejected before application composition", async () => {
  await assert.rejects(
    loadDashboardRuntimeConfiguration(async () => new Response(JSON.stringify({
      ...runtimeConfiguration,
      requestTimeoutMilliseconds: 0,
    }), { status: 200 })),
    /malformed/,
  );
});

test("all query states have deterministic presentation without metric interpretation", () => {
  const problem = { title: "Invalid", detail: "Bad query" };
  const failure = new ReportingNetworkFailure(new Error("offline"));

  assert.deepEqual(presentQueryState({ kind: "idle" }), { kind: "idle", message: "Ready." });
  assert.deepEqual(presentQueryState({ kind: "loading" }), { kind: "loading", message: "Loading." });
  assert.deepEqual(
    presentQueryState({ kind: "success", data: { items: [{ value: 0 }] } }),
    { kind: "success", message: "Data loaded." },
  );
  assert.deepEqual(presentQueryState({ kind: "empty" }), { kind: "empty", message: "No matching data." });
  assert.deepEqual(
    presentQueryState({ kind: "invalidRequest", details: problem }),
    { kind: "invalidRequest", message: "Bad query" },
  );
  assert.deepEqual(
    presentQueryState({ kind: "failed", failure }),
    { kind: "failed", message: failure.message },
  );
});
