import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { SupervisorEntryReadiness } = await vite.ssrLoadModule('/src/presentation/SupervisorEntryReadiness.tsx');
after(() => vite.close());
const machineId = '677b030b-1ede-4a67-82ae-6a494791357f';
const entryId = 'a1424a6e-0dd0-4bb3-a26b-11c9a48a8aa2';
const valid = {
  classification: 'SupervisorQualificationReadinessDiagnosticOnly',
  machineId, entryId, currentEvidenceId: entryId,
  sourceKind: 'SupervisorEntry', localReviewDecision: 'Approved',
  readinessStatus: 'QualificationReceiptMissing', publicationAuthorized: false,
  warning: 'Diagnostic only'
};
function inspect(h) { return [...h.document.querySelectorAll('button')].find(x => x.textContent.includes('qualification blockers')); }
async function flush() { await act(async () => { await new Promise(resolve => setImmediate(resolve)); }); }

test('on-demand SQL inspection shows blocker without claiming local approval as publication', async t => {
  const previous = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url) => { calls.push(String(url)); return Response.json(valid); };
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(SupervisorEntryReadiness, { machineId, entryId }));
  t.after(() => h.dispose());
  assert.equal(calls.length, 0);
  await h.click(inspect(h));
  await flush();
  assert.equal(calls.length, 1);
  assert.match(calls[0], /production-entries\/.*\/qualification-readiness/);
  assert.match(h.document.body.textContent, /QualificationReceiptMissing/);
  assert.match(h.document.body.textContent, /Publication not authorized/);
});

test('failed refresh removes previously shown qualification blocker', async t => {
  const previous = globalThis.fetch;
  let fail = false;
  globalThis.fetch = async () => fail ? new Response('', { status: 503 }) : Response.json(valid);
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(SupervisorEntryReadiness, { machineId, entryId }));
  t.after(() => h.dispose());
  await h.click(inspect(h));
  await flush();
  assert.match(h.document.body.textContent, /QualificationReceiptMissing/);
  fail = true;
  await h.click(inspect(h));
  await flush();
  assert.doesNotMatch(h.document.body.textContent, /QualificationReceiptMissing/);
  assert.match(h.document.body.textContent, /Qualification status unavailable/);
});

test('mismatched machine or positive authority response fails closed', async t => {
  const previous = globalThis.fetch;
  let response = { ...valid, machineId: '00000000-0000-0000-0000-000000000001' };
  globalThis.fetch = async () => Response.json(response);
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(SupervisorEntryReadiness, { machineId, entryId }));
  t.after(() => h.dispose());
  await h.click(inspect(h));
  await flush();
  assert.match(h.document.body.textContent, /Qualification status unavailable/);
  response = { ...valid, publicationAuthorized: true };
  await h.click(inspect(h));
  await flush();
  assert.match(h.document.body.textContent, /Qualification status unavailable/);
  assert.doesNotMatch(h.document.body.textContent, /Publication not authorized/);
});
