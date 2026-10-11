import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { MachineActivityReportPage } = await vite.ssrLoadModule('/src/presentation/MachineActivityReportPage.tsx');
after(() => vite.close());
const machineId = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
function fixture(range) {
  const states = ['unknown','stopped','idle','running','fault','offline'];
  return { machineId, stateProcessorId: 'machine-state-activity', streamKey: 'activity', throughPosition: '10', ...range,
    readAsOf: '2026-10-11T00:00:00Z', unrepresentedTicks: 858000000000,
    totals: states.map(state => ({state, durationTicks: state === 'running' ? 6000000000 : 0})),
    intervals: [{position: '1', state: 'running', startedAtUtc: '2026-10-06T01:00:00Z', endedAtUtc: '2026-10-06T01:10:00Z',
      sourceStartedAtUtc: '2026-10-06T01:00:00Z', sourceEndedAtUtc: '2026-10-06T01:10:00Z', durationTicks: 6000000000, instanceId: '1', sequence: '2'}] };
}
test('machine activity selection renders a timeline and exact accessible interval details; failure returns no report', async t => {
  const original = globalThis.fetch; t.after(() => {globalThis.fetch = original;});
  let failed = false; let sent;
  globalThis.fetch = async (_, options) => {sent = JSON.parse(options.body); return failed ? new Response('', {status: 503}) : Response.json(fixture(sent));};
  const h = await mountInDom(React.createElement(MachineActivityReportPage, {machineId, timeoutMilliseconds: 1000}));
  t.after(() => h.dispose());
  const inputs = h.document.querySelectorAll('input[type=date]');
  assert.match(inputs[0].value, /^\d{4}-\d{2}-\d{2}$/);
  assert.equal(inputs[0].value, inputs[1].value);
  assert.equal(h.document.querySelector('svg'), null);
  await h.changeInput(inputs[0], '2026-10-06'); await h.changeInput(inputs[1], '2026-10-06'); await h.submit(inputs[0].form);
  assert.equal(sent.fromInclusive, '2026-10-05T18:30:00.000Z');
  assert.ok(h.document.querySelector('svg'));
  assert.equal(h.document.querySelectorAll('tbody tr').length, 1);
  assert.equal(h.document.querySelector('[aria-label="Exact stored activity intervals"]').tabIndex, 0);
  assert.match(h.document.body.textContent, /06\/10\/2026 06:30:00 IST/);
  assert.match(h.document.body.textContent, /6000000000 ticks/);
  assert.match(h.document.body.textContent, /No stored interval/);
  failed = true; await h.submit(inputs[0].form);
  assert.match(h.document.querySelector('[role=alert]').textContent, /503/);
  assert.equal(h.document.querySelector('svg'), null); assert.equal(h.document.querySelector('table'), null);
});
