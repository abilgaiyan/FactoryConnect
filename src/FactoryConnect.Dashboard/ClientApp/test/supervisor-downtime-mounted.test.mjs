import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { SupervisorDowntimePanel } = await vite.ssrLoadModule('/src/presentation/SupervisorDowntimePanel.tsx');
after(() => vite.close());

const machineId = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
const interval = (position, state, clipped = false) => ({
  position, state, startedAtUtc: '2026-10-06T01:00:00Z', endedAtUtc: '2026-10-06T01:10:00Z',
  sourceStartedAtUtc: clipped ? '2026-10-06T00:50:00Z' : '2026-10-06T01:00:00Z',
  sourceEndedAtUtc: '2026-10-06T01:10:00Z', durationTicks: 6000000000, instanceId: '1', sequence: '2'
});
const report = {
  machineId, stateProcessorId: 'machine-state-activity', streamKey: 'activity', throughPosition: '100',
  fromInclusive: '2026-10-05T18:30:00Z', toExclusive: '2026-10-06T18:30:00Z',
  readAsOf: '2026-10-07T00:00:00Z', unrepresentedTicks: 0, totals: [],
  intervals: [interval('1', 'stopped'), interval('2', 'running'), interval('3', 'fault', true),
    interval('9007199254740992', 'offline'), interval('5', 'offline')]
};

test('downtime panel offers only complete, safely representable downtime periods and reads both ledgers', async t => {
  const original = globalThis.fetch;
  t.after(() => { globalThis.fetch = original; });
  const urls = [];
  globalThis.fetch = async url => { urls.push(String(url)); return Response.json([]); };
  const h = await mountInDom(React.createElement(SupervisorDowntimePanel, { report }));
  t.after(() => h.dispose());
  const choices = [...h.document.querySelectorAll('label select option')].map(option => option.value);
  assert.deepEqual(choices, ['', '1', '5']);
  assert.equal(urls.length, 3);
  assert.ok(urls.some(url => url.includes('/downtime-reasons')));
  assert.ok(urls.some(url => url.includes('/downtime-explanations?')));
  assert.ok(urls.some(url => url.includes('/downtime-reviews?')));
  assert.match(h.document.body.textContent, /local commissioning only/i);
});
