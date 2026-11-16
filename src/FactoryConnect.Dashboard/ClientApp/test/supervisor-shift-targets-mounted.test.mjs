import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { SupervisorLocalShiftTargets } = await vite.ssrLoadModule('/src/presentation/SupervisorLocalShiftTargets.tsx');
after(() => vite.close());
const scope = { machineId: '677b030b-1ede-4a67-82ae-6a494791357f', shiftScheduleAssignmentId: 'assignment-a', shiftId: 'SHIFT-3', startsAtUtc: '2026-10-28T18:30:00Z', endsAtUtc: '2026-10-29T02:00:00Z' };
function button(h, name) { return [...h.document.querySelectorAll('button')].find(x => x.textContent === name); }
function input(h, name) { return [...h.document.querySelectorAll('label')].find(x => x.textContent.startsWith(name)).querySelector('input,textarea'); }

test('SQL target editor creates, revises, and reconciles ambiguous save without duplicate revision', async t => {
  const previous = globalThis.fetch;
  const history = [];
  const writes = [];
  let ambiguous = false;
  globalThis.fetch = async (url, options = {}) => {
    if (options.method !== 'POST') return Response.json(history);
    const request = JSON.parse(options.body);
    writes.push(request);
    if (!history.some(x => x.targetRevisionId === request.targetRevisionId))
      history.push({ ...request, machineId: scope.machineId, enteredBy: 'local-supervisor', recordedAtUtc: '2026-11-16T10:00:00Z' });
    if (ambiguous) { ambiguous = false; throw new Error('Connection dropped after commit'); }
    return Response.json({ revisionId: request.targetRevisionId });
  };
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(SupervisorLocalShiftTargets, { scope }));
  t.after(() => h.dispose());
  await act(async () => { await new Promise(resolve => setImmediate(resolve)); });
  assert.match(h.document.body.textContent, /TargetMissing/);
  await h.changeInput(input(h, 'Target quantity'), '125.500');
  await h.click(button(h, 'Save target revision'));
  assert.equal(history.length, 1);
  assert.equal(writes[0].revisionNumber, 1);
  assert.equal(writes[0].expectedHeadRevisionId, null);
  assert.match(h.document.body.textContent, /Revision 1/);
  await h.changeInput(input(h, 'Target quantity'), '150');
  await h.changeInput(input(h, 'Revision reason'), 'Updated shift plan');
  ambiguous = true;
  await h.click(button(h, 'Save target revision'));
  assert.equal(history.length, 2);
  assert.match(h.document.body.textContent, /Save outcome is uncertain/);
  await h.click(button(h, 'Reload target history'));
  assert.equal(history.length, 2);
  assert.match(h.document.body.textContent, /Previously uncertain save confirmed/);
  assert.doesNotMatch(h.document.body.textContent, /Save outcome is uncertain/);
  assert.equal(writes[1].revisionNumber, 2);
  assert.equal(writes[1].expectedHeadRevisionId, history[0].targetRevisionId);
  assert.equal(writes[1].revisionReason, 'Updated shift plan');
  assert.match(h.document.body.textContent, /planning only, not accepted production or OEE evidence/i);
});
