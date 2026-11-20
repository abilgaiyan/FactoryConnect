import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { SupervisorTargetAchievement } = await vite.ssrLoadModule('/src/presentation/SupervisorTargetAchievement.tsx');
after(() => vite.close());
const machine = { machineId: '677b030b-1ede-4a67-82ae-6a494791357f' };
const shift = { shift: { shiftScheduleAssignmentId: 'assignment', shiftId: 'S1', startsAtUtc: '2026-11-17T02:00:00Z', endsAtUtc: '2026-11-17T10:30:00Z' } };
const common = {
  machineId: machine.machineId, shiftScheduleAssignmentId: shift.shift.shiftScheduleAssignmentId,
  shiftId: shift.shift.shiftId, shiftStartsAtUtc: shift.shift.startsAtUtc,
  shiftEndsAtUtc: shift.shift.endsAtUtc, productionOrder: 'ORDER', partNumber: 'PART',
  operation: 'HOB', quantityUnit: 'pieces'
};
const target = { ...common, targetRevisionId: 'revision-1', revisionNumber: 1, targetQuantity: 200 };
const actual = { ...common, classification: 'AcceptedShiftProduction', publicationId: 'publication-1',
  coverage: 'Complete', acceptedProducedQuantity: 150 };
async function settle() { await act(async () => { await new Promise(resolve => setImmediate(resolve)); }); }
function mock(t, getTarget, getActual) {
  const previous = globalThis.fetch;
  globalThis.fetch = async url => Response.json(String(url).includes('/shift-targets?') ? getTarget() : getActual());
  t.after(() => { globalThis.fetch = previous; });
}

test('displays achievement only for exact matching SQL target and accepted publication', async t => {
  mock(t, () => [target], () => actual);
  const h = await mountInDom(React.createElement(SupervisorTargetAchievement, { machine, shift }));
  t.after(() => h.dispose()); await settle();
  assert.match(h.document.body.textContent, /75.0% achieved/);
  assert.match(h.document.body.textContent, /150 accepted \/ 200 planned/);
  assert.match(h.document.body.textContent, /Target revision 1/);
});

test('missing accepted publication is unavailable rather than zero achievement', async t => {
  mock(t, () => [target], () => ({ classification: 'AcceptedProductionUnavailable', reason: 'NoQualifiedPublicationForShift' }));
  const h = await mountInDom(React.createElement(SupervisorTargetAchievement, { machine, shift }));
  t.after(() => h.dispose()); await settle();
  assert.match(h.document.body.textContent, /Achievement unavailable: AcceptedProductionMissing/);
  assert.doesNotMatch(h.document.body.textContent, /% achieved/);
});

test('context mismatch and duplicate target revisions fail closed', async t => {
  let targets = [target];
  mock(t, () => targets, () => ({ ...actual, operation: 'OTHER' }));
  const h = await mountInDom(React.createElement(SupervisorTargetAchievement, { machine, shift }));
  t.after(() => h.dispose()); await settle();
  assert.match(h.document.body.textContent, /ProductionContextMismatch/);
  targets = [target, { ...target, targetRevisionId: 'revision-2' }];
  await h.click([...h.document.querySelectorAll('button')].find(x => x.textContent === 'Refresh target achievement'));
  await settle();
  assert.match(h.document.body.textContent, /Conflicting target revisions/);
  assert.doesNotMatch(h.document.body.textContent, /% achieved/);
});


test('mismatched accepted publication fails closed even when no target revision exists', async t => {
  mock(t, () => [], () => ({ ...actual, shiftScheduleAssignmentId: 'different-assignment' }));
  const h = await mountInDom(React.createElement(SupervisorTargetAchievement, { machine, shift }));
  t.after(() => h.dispose()); await settle();
  assert.match(h.document.body.textContent, /Accepted production publication does not match selected shift occurrence/);
  assert.doesNotMatch(h.document.body.textContent, /% achieved/);
  assert.doesNotMatch(h.document.body.textContent, /TargetMissing/);
});


test('duplicate target revision identity with different revision numbers fails closed', async t => {
  mock(t, () => [target, { ...target, revisionNumber: 2 }], () => actual);
  const h = await mountInDom(React.createElement(SupervisorTargetAchievement, { machine, shift }));
  t.after(() => h.dispose()); await settle();
  assert.match(h.document.body.textContent, /Conflicting target revisions/);
  assert.doesNotMatch(h.document.body.textContent, /% achieved/);
});
