import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { SupervisorAcceptedProductionReadout } = await vite.ssrLoadModule('/src/presentation/SupervisorAcceptedProductionReadout.tsx');
after(() => vite.close());
const machine = { machineId: '677b030b-1ede-4a67-82ae-6a494791357f' };
const shift = { shift: { shiftScheduleAssignmentId: 'assignment', shiftId: 'S1', startsAtUtc: '2026-11-17T02:00:00Z', endsAtUtc: '2026-11-17T10:30:00Z' } };
const publication = {
  classification: 'AcceptedShiftProduction', publicationId: '11111111-1111-4111-8111-111111111111',
  machineId: machine.machineId, shiftScheduleAssignmentId: shift.shift.shiftScheduleAssignmentId,
  shiftId: shift.shift.shiftId, shiftStartsAtUtc: shift.shift.startsAtUtc,
  shiftEndsAtUtc: shift.shift.endsAtUtc, productionOrder: 'ORDER', partNumber: 'PART',
  operation: 'HOB', quantityUnit: 'pieces', acceptedProducedQuantity: 125,
  coverage: 'Complete', authorityReference: 'qualified-source-1', publishedAtUtc: '2026-11-17T11:00:00Z'
};
async function settle() { await act(async () => { await new Promise(resolve => setImmediate(resolve)); }); }

test('accepted actual readout distinguishes missing publication from zero production', async t => {
  const previous = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ classification: 'AcceptedProductionUnavailable', reason: 'NoQualifiedPublicationForShift' });
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(SupervisorAcceptedProductionReadout, { machine, shift }));
  t.after(() => h.dispose());
  await settle();
  assert.match(h.document.body.textContent, /Accepted actual: unavailable.*NoQualifiedPublicationForShift/);
  assert.match(h.document.body.textContent, /does not mean zero production/);
});

test('accepted actual readout rejects mismatched publication and shows matching accepted evidence', async t => {
  const previous = globalThis.fetch;
  let result = { ...publication, shiftId: 'WRONG' };
  globalThis.fetch = async () => Response.json(result);
  t.after(() => { globalThis.fetch = previous; });
  const h = await mountInDom(React.createElement(SupervisorAcceptedProductionReadout, { machine, shift }));
  t.after(() => h.dispose());
  await settle();
  assert.match(h.document.body.textContent, /invalid or mismatched/);
  result = publication;
  await h.click([...h.document.querySelectorAll('button')].find(x => x.textContent === 'Refresh accepted production'));
  await settle();
  assert.match(h.document.body.textContent, /125 pieces/);
  assert.match(h.document.body.textContent, /qualified-source-1/);
  assert.match(h.document.body.textContent, /Target achievement and OEE remain separate/);
});
