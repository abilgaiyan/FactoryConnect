import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { calculateAcceptedTargetAttainment: calculate } = await vite.ssrLoadModule('/src/application/accepted-shift-target-attainment.ts');
after(() => vite.close());

const scope = {
  machineId: 'machine-1', shiftScheduleAssignmentId: 'assignment-1', shiftId: 'S1',
  shiftStartsAtUtc: '2026-11-17T02:00:00Z', shiftEndsAtUtc: '2026-11-17T10:30:00Z',
  productionOrder: 'ORDER-1', partNumber: 'PART-1', operation: 'HOBBING', quantityUnit: 'pieces'
};
const target = { ...scope, targetQuantity: 200 };
const accepted = { ...scope, classification: 'AcceptedShiftProduction', publicationId: 'publication-1',
  coverage: 'Complete', acceptedProducedQuantity: 150 };

test('calculates achievement only from complete published accepted quantity', () => {
  assert.deepEqual(calculate(target, accepted), {
    state: 'calculated', acceptedActual: 150, target: 200, percent: 75, publicationId: 'publication-1'
  });
});

test('fails closed for absent acceptance, publication, coverage, or mismatched occurrence/context/units', () => {
  const cases = [
    [null, accepted, 'TargetMissing'],
    [target, null, 'AcceptedProductionMissing'],
    [target, { ...accepted, classification: 'HistoricalMetricInputSourceInventoryNotQualifiedActual' }, 'AcceptedPublicationMissing'],
    [target, { ...accepted, publicationId: '' }, 'AcceptedPublicationMissing'],
    [target, { ...accepted, coverage: 'Incomplete' }, 'ProductionCoverageNotComplete'],
    [target, { ...accepted, coverage: 'Conflicted' }, 'ProductionCoverageNotComplete'],
    [target, { ...accepted, shiftStartsAtUtc: '2026-11-16T02:00:00Z' }, 'ShiftOccurrenceMismatch'],
    [target, { ...accepted, productionOrder: 'OTHER' }, 'ProductionContextMismatch'],
    [target, { ...accepted, quantityUnit: 'kg' }, 'QuantityUnitMismatch'],
    [target, { ...accepted, acceptedProducedQuantity: -1 }, 'AcceptedQuantityInvalid'],
    [{ ...target, targetQuantity: 0 }, accepted, 'TargetNotPositive']
  ];
  for (const [plan, actual, reason] of cases)
    assert.deepEqual(calculate(plan, actual), { state: 'unavailable', reason });
});
