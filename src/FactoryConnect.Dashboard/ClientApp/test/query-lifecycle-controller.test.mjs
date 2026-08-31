import assert from "node:assert/strict";
import test from "node:test";

import {
  ReportingCancellationFailure,
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingNetworkFailure,
  ReportingProtocolFailure,
  ReportingTimeoutFailure,
} from "../src/api/reporting/index.ts";
import { createQueryLifecycleController } from "../src/query/query-lifecycle-controller.ts";

const problemDetails = {
  type: "urn:factoryconnect:problem:reporting:invalid-request",
  title: "Invalid reporting query",
  status: 400,
  detail: "The query is invalid.",
  instance: null,
};

test("controller starts idle and publishes loading then success", async () => {
  const controller = createQueryLifecycleController({
    query: async () => ({ items: [1] }),
    isEmpty: (data) => data.items.length === 0,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  assert.deepEqual(controller.current(), { kind: "idle" });
  const result = await controller.execute();

  assert.deepEqual(states, ["loading", "success"]);
  assert.equal(result.kind, "success");
  assert.deepEqual(controller.current(), result);
});

test("successful zero-item result becomes empty", async () => {
  const controller = createQueryLifecycleController({
    query: async () => ({ items: [] }),
    isEmpty: (data) => data.items.length === 0,
  });

  assert.deepEqual(await controller.execute(), { kind: "empty" });
});

test("calculated zero remains successful data", async () => {
  const data = { items: [{ status: "calculated", value: 0 }] };
  const controller = createQueryLifecycleController({
    query: async () => data,
    isEmpty: (page) => page.items.length === 0,
  });

  assert.deepEqual(await controller.execute(), { kind: "success", data });
});

test("invalid reporting query becomes invalidRequest with original Problem Details", async () => {
  const failure = new ReportingInvalidQueryFailure(problemDetails);
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(failure),
    isEmpty: () => false,
  });

  const result = await controller.execute();
  assert.deepEqual(result, { kind: "invalidRequest", details: problemDetails });
  assert.equal(result.details, failure.problemDetails);
});

test("all non-invalid reporting failures remain failed", async () => {
  const failures = [
    new ReportingCancellationFailure(),
    new ReportingTimeoutFailure(30_000),
    new ReportingNetworkFailure(new Error("offline")),
    new ReportingMalformedContinuationTokenFailure({ ...problemDetails, type: "urn:factoryconnect:problem:reporting:malformed-continuation-token" }),
    new ReportingIncompatibleContinuationTokenFailure({ ...problemDetails, type: "urn:factoryconnect:problem:reporting:incompatible-continuation-token" }),
    new ReportingHttpFailure(502),
    new ReportingProtocolFailure(200, "Malformed response."),
  ];

  for (const failure of failures) {
    const controller = createQueryLifecycleController({
      query: async () => Promise.reject(failure),
      isEmpty: () => false,
    });

    const result = await controller.execute();
    assert.equal(result.kind, "failed");
    assert.equal(result.failure, failure);
  }
});

test("failure never becomes empty", async () => {
  const failure = new ReportingNetworkFailure(new Error("offline"));
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(failure),
    isEmpty: () => true,
  });

  const result = await controller.execute();
  assert.equal(result.kind, "failed");
  assert.equal(result.failure, failure);
});

test("unexpected cancellation is observable as failed in E.4", async () => {
  const failure = new ReportingCancellationFailure("unexpected");
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(failure),
    isEmpty: () => false,
  });

  const result = await controller.execute();
  assert.deepEqual(result, { kind: "failed", failure });
});

test("sequential re-execution replaces prior terminal state through loading", async () => {
  const responses = [
    { items: [1] },
    { items: [] },
    { items: [2] },
  ];
  let index = 0;
  const controller = createQueryLifecycleController({
    query: async () => responses[index++],
    isEmpty: (data) => data.items.length === 0,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  assert.equal((await controller.execute()).kind, "success");
  assert.equal((await controller.execute()).kind, "empty");
  assert.equal((await controller.execute()).kind, "success");

  assert.deepEqual(states, [
    "loading",
    "success",
    "loading",
    "empty",
    "loading",
    "success",
  ]);
  assert.deepEqual(controller.current(), {
    kind: "success",
    data: { items: [2] },
  });
});

test("subscription cleanup stops later notifications", async () => {
  let value = 1;
  const controller = createQueryLifecycleController({
    query: async () => ({ items: [value++] }),
    isEmpty: () => false,
  });
  const states = [];
  const dispose = controller.subscribe((state) => states.push(state.kind));

  await controller.execute();
  dispose();
  await controller.execute();

  assert.deepEqual(states, ["loading", "success"]);
});

test("unexpected programming exceptions are rethrown and do not masquerade as reporting failures", async () => {
  const defect = new TypeError("bug");
  const controller = createQueryLifecycleController({
    query: async () => Promise.reject(defect),
    isEmpty: () => false,
  });
  const states = [];
  controller.subscribe((state) => states.push(state.kind));

  await assert.rejects(controller.execute(), (error) => error === defect);
  assert.deepEqual(states, ["loading", "idle"]);
  assert.deepEqual(controller.current(), { kind: "idle" });
});
