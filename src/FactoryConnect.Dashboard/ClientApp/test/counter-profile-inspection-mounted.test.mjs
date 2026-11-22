import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import React from "react";
import { createServer } from "vite";
import { mountInDom } from "./dom-test-harness.mjs";

const vite = await createServer({
  root: fileURLToPath(new URL("../", import.meta.url)),
  server: { middlewareMode: true }, appType: "custom", logLevel: "silent"
});
const { CounterProfileInspection } =
  await vite.ssrLoadModule("/src/presentation/CounterProfileInspection.tsx");
after(() => vite.close());
const machineId = "677b030b-1ede-4a67-82ae-6a494791357f";
const atUtc = "2026-11-20T18:30:00.000Z";
const props = { machineId, atUtc };

test("profile inspection is opt-in and does not infer counter mapping", async t => {
  const original = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async url => {
    requests.push(String(url));
    return Response.json({
      profileRevisionId: "d14f2d7d-2cb9-47ce-932f-ae1f3c3c512e",
      revisionNumber: 2, evidenceMode: "CncCounterAndState",
      stateDataItemId: "execution", counterDataItemId: "part_count",
      counterSemantics: "CumulativeResettable", effectiveFromUtc: atUtc
    });
  };
  t.after(() => { globalThis.fetch = original; });
  const h = await mountInDom(React.createElement(CounterProfileInspection, props));
  t.after(() => h.dispose());
  assert.equal(requests.length, 0);
  await h.click(h.document.querySelector("button"));
  assert.equal(requests.length, 1);
  assert.match(requests[0], /evidence-profile\?atUtc=/);
  assert.match(h.document.body.textContent, /CncCounterAndState/);
  assert.match(h.document.body.textContent, /part_count/);
  assert.match(h.document.body.textContent, /does not assert that they refer to the same signal/);
  assert.match(h.document.body.textContent, /No raw signals discovered/);
});

test("unavailable local commissioning read does not fabricate profile", async t => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => new Response("", { status: 403 });
  t.after(() => { globalThis.fetch = original; });
  const h = await mountInDom(React.createElement(CounterProfileInspection, props));
  t.after(() => h.dispose());
  await h.click(h.document.querySelector("button"));
  assert.match(h.document.body.textContent, /HTTP 403/);
  assert.doesNotMatch(h.document.body.textContent, /Configured counter data item ID/);
});

test("literal raw address match is a candidate, never a verified mapping", async t => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => Response.json({
    profileRevisionId: "d14f2d7d-2cb9-47ce-932f-ae1f3c3c512e",
    revisionNumber: 1, evidenceMode: "CounterOnly",
    stateDataItemId: null, counterDataItemId: "part_count",
    counterSemantics: "CumulativeMonotonic", effectiveFromUtc: atUtc
  });
  t.after(() => { globalThis.fetch = original; });
  const h = await mountInDom(React.createElement(CounterProfileInspection, {
    ...props, availableSignals: [{ source: "mtconnect", address: "part_count" }]
  }));
  t.after(() => h.dispose());
  await h.click(h.document.querySelector("button"));
  assert.match(h.document.body.textContent, /candidate only; mapping unverified/);
  assert.match(h.document.body.textContent, /Matching addresses do not prove/);
});
