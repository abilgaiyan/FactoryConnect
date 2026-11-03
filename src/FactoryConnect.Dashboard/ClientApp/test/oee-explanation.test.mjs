import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { OeeExplanation } from '../src/application/OeeExplanation.ts';

function machine(position = '18446744073709551615') {
  const missing = key => ({ kind: 'missing', metricKey: key, version: '1.0' });
  return { displayName: 'GG-003', machineId: 'machine-1', processorId: 'reporter', metrics: {
    availability: { kind: 'calculated', metricKey: 'availability', version: '1.0', value: '0', unit: 'ratio', sourceRevision: { machineId: 'machine-1', processorId: 'aggregator', streamKey: 'facts', position } },
    performance: { kind: 'insufficient-evidence', metricKey: 'performance', version: '1.0', reasonCode: 'MissingReferenceTime', reasonOperandName: 'ProductionReferenceTime', sourceRevision: { machineId: 'machine-1', processorId: 'aggregator', streamKey: 'facts', position } },
    quality: missing('quality'), oee: missing('oee'),
  } };
}
const render = value => renderToStaticMarkup(React.createElement(OeeExplanation, { machine: value, productionDay: '2026-11-02' }));
test('explanation preserves zero, exact UInt64 checkpoint and missing reasons without invented operands', () => {
  const html = render(machine());
  assert.match(html, /18446744073709551615/);
  assert.match(html, /Reported value: 0 ratio/);
  assert.match(html, /MissingReferenceTime/);
  assert.match(html, /ProductionReferenceTime/);
  assert.match(html, /Reported status: missing/);
  assert.match(html, /Evidence verification not established/);
  assert.match(html, /Operand values and underlying evidence references are not supplied/);
  assert.match(html, /2026-11-02/);
  assert.doesNotMatch(html, /80%|360|450/);
});
test('explanation does not present an unsafe number as an exact checkpoint identity', () => {
  assert.match(render(machine(18446744073709551615)), /Exact identity unavailable/);
});

test('mounted overview details follow the displayed report across replacement without network requests', async () => {
  const { mountInDom } = await import('./dom-test-harness.mjs');
  const { ProductionDayOverviewMatrix } = await import('../src/application/ProductionDayOverviewMatrix.ts');
  const { act } = await import('react');
  const first = machine('41');
  const model = m => ({ productionDay: '2026-11-02', groups: [{ groupName: null, machines: [{ ...m, metrics: { ...m.metrics, utilization: { kind: 'missing', metricKey: 'utilization.elr', version: '1.0' } } }] }] });
  let replace;
  function Harness() {
    const [value, setValue] = React.useState(first);
    replace = setValue;
    return React.createElement(ProductionDayOverviewMatrix, { model: model(value) });
  }
  const mounted = await mountInDom(React.createElement(Harness));
  try {
    assert.match(mounted.container.querySelector('summary').getAttribute('aria-label'), /GG-003/);
    const details = mounted.container.querySelector('details');
    details.open = true;
    assert.match(details.textContent, /position 41/);
    // Render the next displayed response; the explanation must not retain the old checkpoint.
    await act(async () => replace(machine('42')));
    assert.match(mounted.container.querySelector('details').textContent, /position 42/);
    assert.doesNotMatch(mounted.container.querySelector('details').textContent, /position 41/);
  } finally { await mounted.dispose(); }
});
