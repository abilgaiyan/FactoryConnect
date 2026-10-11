import { test } from 'node:test';
import assert from 'node:assert/strict';
import { activityDateRange } from '../src/application/machine-activity-report.ts';
import { decodeCounterReport, loadCounterReport } from '../src/application/machine-counter-report.ts';
const id = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
const selection = { ...activityDateRange('2026-10-06', '2026-10-06'), source: 'mtconnect', address: 'part_count' };
const fixture = () => ({ machineId: id, streamKey: 'mtconnect:M01', throughPosition: '9007199254740995', acquisitionRevision: '2',
  ...selection, readAsOf: '2026-10-11T00:00:00Z', availableSignals: [{ source: 'mtconnect', address: 'part_count' }], samples: [{
    position: '9007199254740994', instanceId: '1', sequence: '2', observedAtUtc: '2026-10-06T00:00:00.1234567Z',
    signalType: 'Numeric', quality: 'Good', rawValue: '9007199254740993', previousPosition: '9007199254740993', arithmeticDifference: '2',
    flags: ['ContinuityNotEstablished', 'AttributionNotEstablished'],
  }] });
test('exact raw strings and provenance survive; no conversion to JS counts', () => {
  const r = decodeCounterReport(fixture(), id, selection);
  assert.equal(r.samples[0].rawValue, '9007199254740993'); assert.equal(r.samples[0].observedAtUtc, '2026-10-06T00:00:00.1234567Z');
});
test('failed or mismatched responses cannot become an empty report', () => {
  for (const mutate of [r => r.machineId = 'different', r => r.address = 'PART_COUNT', r => r.samples[0].position = '9007199254740996',
    r => r.samples.push({...r.samples[0]}), r => r.samples[0].observedAtUtc = selection.toExclusive,
    r => r.samples[0].flags = [], r => r.samples[0].previousPosition = r.samples[0].position]) {
    const f = fixture(); mutate(f); assert.throws(() => decodeCounterReport(f, id, selection));
  }
});
test('missing or unusable values stay explicit and cannot carry differences', () => {
  const f = fixture(); f.samples[0].rawValue = null; f.samples[0].arithmeticDifference = null; f.samples[0].flags.push('MissingValue');
  assert.equal(decodeCounterReport(f, id, selection).samples[0].rawValue, null);
  f.samples[0].arithmeticDifference = '2'; assert.throws(() => decodeCounterReport(f, id, selection));
});
test('successful empty selection is distinct from failed retrieval', async () => {
  const f = fixture(); f.samples = []; assert.equal(decodeCounterReport(f, id, selection).samples.length, 0);
  await assert.rejects(loadCounterReport(id, selection, new AbortController().signal, async () => new Response('', {status:503})), /503/);
});
test('exact source selection is sent and cancelled late replies are rejected', async () => {
  const controller = new AbortController(); let finish; let sent;
  const pending = loadCounterReport(id, selection, controller.signal, (url, options) => {
    sent = {url, options}; return new Promise(resolve => {finish = resolve;});
  });
  assert.deepEqual(JSON.parse(sent.options.body), selection); assert.equal(sent.options.signal, controller.signal);
  controller.abort(); finish(Response.json(fixture())); await assert.rejects(pending, e => e.name === 'AbortError');
});
