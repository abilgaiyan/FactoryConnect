import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)),
  server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { reconcileQualifiedProduction } = await vite.ssrLoadModule(
  '/src/application/qualified-production-reconciliation.ts');
after(() => vite.close());

const scope = {
  machineId: 'machine-1', shiftScheduleAssignmentId: 'assignment-1',
  shiftId: 'shift-1', shiftStartsAtUtc: '2026-11-20T02:00:00Z',
  shiftEndsAtUtc: '2026-11-20T10:30:00Z', quantityUnit: 'pieces'
};
const contribution = {
  ...scope, evidenceId: 'evidence-1', populationKey: 'population-1',
  authorityReference: 'qualification-1', qualification: 'Qualified',
  produced: 10, good: 8, rejected: 2
};
const held = (items, reason) =>
  assert.deepEqual(reconcileQualifiedProduction(scope, items), { state: 'held', reason });

test('disjoint qualified populations reconcile as a candidate, never a publication', () => {
  assert.deepEqual(reconcileQualifiedProduction(scope, [
    contribution, { ...contribution, evidenceId: 'evidence-2', populationKey: 'population-2',
      produced: 4, good: 4, rejected: 0 }
  ]), {
    state: 'candidate', produced: 14, good: 12, rejected: 2,
    evidenceIds: ['evidence-1', 'evidence-2'], authorityReferences: ['qualification-1']
  });
});

test('unqualified, duplicate, overlapping and cross-shift inputs are held', () => {
  held([], 'NoQualifiedContributions');
  held([{ ...contribution, qualification: 'Pending' }], 'QualificationMissing');
  held([contribution, contribution], 'DuplicateEvidence');
  held([contribution, { ...contribution, evidenceId: 'evidence-2' }], 'PopulationOverlap');
  held([{ ...contribution, shiftId: 'other-shift' }], 'ScopeMismatch');
  held([{ ...contribution, populationKey: '' }], 'ProvenanceMissing');
});

test('inconsistent quantities and unsafe sums are held', () => {
  held([{ ...contribution, good: 9 }], 'QuantityInconsistent');
  held([{ ...contribution, rejected: -1 }], 'QuantityInconsistent');
  held([{ ...contribution, produced: Number.MAX_SAFE_INTEGER, good: Number.MAX_SAFE_INTEGER, rejected: 0 },
    { ...contribution, evidenceId: 'evidence-2', populationKey: 'population-2',
      produced: 1, good: 1, rejected: 0 }], 'QuantityOverflow');
});


test('non-UTC, ambiguous and invalid shift times are held', () => {
  assert.deepEqual(reconcileQualifiedProduction(
    { ...scope, shiftStartsAtUtc: '2026-11-20T07:30:00+05:30' }, [contribution]),
    { state: 'held', reason: 'InvalidShiftScope' });
  assert.deepEqual(reconcileQualifiedProduction(
    { ...scope, shiftStartsAtUtc: '2026-11-20T02:00:00' }, [contribution]),
    { state: 'held', reason: 'InvalidShiftScope' });
  assert.deepEqual(reconcileQualifiedProduction(
    { ...scope, shiftEndsAtUtc: 'not-a-date' }, [contribution]),
    { state: 'held', reason: 'InvalidShiftScope' });
});
