import assert from "node:assert/strict";
import test from "node:test";

import {
  ProductionDayShiftRosterCoverageRequiredFailure,
  ReportingInvalidQueryFailure,
  ReportingNetworkFailure,
} from "../src/api/reporting/index.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function problemDetails() {
  return {
    type: "urn:factoryconnect:problem:reporting:invalid-request",
    title: "Invalid reporting query",
    status: 400,
    detail: "The query is invalid.",
    instance: null,
  };
}

test("refresh from success retains the exact completed result until replacement completes", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: () => {
      const pending = deferred();
      requests.push(pending);
      return pending.promise;
    },
    isEmpty: (data) => data.items.length === 0,
  });

  const first = controller.execute();
  const completed = { items: ["A"] };
  requests[0].resolve(completed);
  await first;

  const refresh = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: completed });

  const replacement = { items: ["B"] };
  requests[1].resolve(replacement);
  assert.deepEqual(await refresh, { kind: "success", data: replacement });
});

test("refresh from authoritative empty retains that exact empty result", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: () => {
      const pending = deferred();
      requests.push(pending);
      return pending.promise;
    },
    isEmpty: (data) => data.items.length === 0,
  });

  const empty = { items: [] };
  const first = controller.execute();
  requests[0].resolve(empty);
  assert.deepEqual(await first, { kind: "empty", data: empty });

  const refresh = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: empty });
  requests[1].resolve({ items: ["B"] });
  await refresh;
});

test("repeated refresh keeps the original last completed result and supersedes the in-flight generation", async () => {
  const requests = [];
  const controller = createQueryLifecycleController({
    query: (signal) => {
      const pending = deferred();
      requests.push({ pending, signal });
      return pending.promise;
    },
    isEmpty: () => false,
  });

  const initial = controller.execute();
  const completed = { items: ["A"] };
  requests[0].pending.resolve(completed);
  await initial;

  const refreshB = controller.execute();
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: completed });
  const refreshC = controller.execute();
  assert.equal(requests[1].signal.aborted, true);
  assert.deepEqual(controller.current(), { kind: "refreshing", previous: completed });

  requests[2].pending.resolve({ items: ["C"] });
  await refreshC;
  requests[1].pending.resolve({ items: ["B"] });
  await refreshB;

  assert.deepEqual(controller.current(), { kind: "success", data: { items: ["C"] } });
});

test("refresh failure terminates previous-result retention", async () => {
  const failure = new ReportingNetworkFailure(new Error("offline"));
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? { items: ["A"] } : Promise.reject(failure),
    isEmpty: () => false,
  });

  await controller.execute();
  assert.equal(controller.current().kind, "success");
  const result = await controller.execute();
  assert.deepEqual(result, { kind: "failed", failure });
});

test("refresh invalid request terminates previous-result retention", async () => {
  const details = problemDetails();
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => ++call === 1 ? { items: ["A"] } : Promise.reject(new ReportingInvalidQueryFailure(details)),
    isEmpty: () => false,
  });

  await controller.execute();
  const result = await controller.execute();
  assert.deepEqual(result, { kind: "invalidRequest", details });
});

test("refresh coverage failure terminates previous-result retention", async () => {
  let call = 0;
  const controller = createQueryLifecycleController({
    query: async () => {
      if (++call === 1) {
        return { items: ["A"] };
      }
      throw new ProductionDayShiftRosterCoverageRequiredFailure("machine-1", "site-1", "2026-09-03");
    },
    isEmpty: () => false,
  });

  await controller.execute();
  const result = await controller.execute();
  assert.deepEqual(result, {
    kind: "coverageRequired",
    details: { machineId: "machine-1", siteId: "site-1", businessDate: "2026-09-03" },
  });
});
