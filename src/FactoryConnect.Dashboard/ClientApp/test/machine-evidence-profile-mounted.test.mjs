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
const { MachineEvidenceProfileAdministration } =
  await vite.ssrLoadModule("/src/presentation/MachineEvidenceProfileAdministration.tsx");
after(() => vite.close());

const machine = "677b030b-1ede-4a67-82ae-6a494791357f";
const profile = {
  profileRevisionId: "d14f2d7d-2cb9-47ce-932f-ae1f3c3c512e",
  machineId: machine, revisionNumber: 1, evidenceMode: "StateOnly",
  stateDataItemId: "execution", counterDataItemId: null, counterSemantics: null,
  effectiveFromUtc: "2026-11-20T00:00:00Z",
  configuredBy: "local", configuredAtUtc: "2026-11-20T00:00:00Z"
};
const button = (h, label) => [...h.document.querySelectorAll("button")]
  .find(item => item.textContent === label);

test("requires an explicit machine UUID before reading or saving", async t => {
  const previous = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => { calls++; return Response.json([]); };
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(MachineEvidenceProfileAdministration));
  t.after(() => h.dispose());
  assert.equal(button(h, "Load machine profiles").disabled, true);
  assert.equal(button(h, "Save new revision"), undefined);
  assert.equal(calls, 0);
});

test("loads history and does not offer a counter for state-only mode", async t => {
  const previous = globalThis.fetch;
  const urls = [];
  globalThis.fetch = async (url) => { urls.push(String(url)); return Response.json([profile]); };
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(MachineEvidenceProfileAdministration));
  t.after(() => h.dispose());
  const machineInput = h.document.querySelector("input");
  await h.changeInput(machineInput, machine);
  await h.click(button(h, "Load machine profiles"));
  assert.equal(urls.length, 1);
  assert.match(urls[0], new RegExp(machine));
  assert.match(h.document.body.textContent, /Append revision 2/);
  assert.match(h.document.body.textContent, /execution/);
  assert.equal(h.document.body.textContent.includes("MTConnect counter data item ID"), false);
});

test("failed history read never exposes an editor with assumed defaults", async t => {
  const previous = globalThis.fetch;
  globalThis.fetch = async () => new Response("", { status: 503 });
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(MachineEvidenceProfileAdministration));
  t.after(() => h.dispose());
  await h.changeInput(h.document.querySelector("input"), machine);
  await h.click(button(h, "Load machine profiles"));
  assert.match(h.document.body.textContent, /HTTP 503/);
  assert.equal(button(h, "Save new revision"), undefined);
});

test("selects configured machine by display name without inventing evidence mode", async t => {
  const previous = globalThis.fetch;
  const requested = [];
  globalThis.fetch = async url => { requested.push(String(url)); return Response.json([]); };
  t.after(() => { globalThis.fetch = previous; });
  const sources = [{
    machineId: machine, displayName: "GG-003", siteId: "Gajra",
    productionLineId: "line-1", processorId: "processor", groupName: null, displayOrder: 0
  }];
  const h = await mountInDom(React.createElement(MachineEvidenceProfileAdministration, { sources }));
  t.after(() => h.dispose());
  const select = h.document.querySelector("select");
  assert.match(select.textContent, /GG-003/);
  await h.changeInput(select, machine);
  await h.click(button(h, "Load machine profiles"));
  assert.equal(requested.length, 1);
  assert.match(requested[0], new RegExp(machine));
  assert.match(h.document.body.textContent, /Unconfigured/);
});
