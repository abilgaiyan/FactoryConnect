import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({root: fileURLToPath(new URL('../', import.meta.url)), server: {middlewareMode:true}, appType:'custom', logLevel:'silent'});
const { MachineCounterReportPage } = await vite.ssrLoadModule('/src/presentation/MachineCounterReportPage.tsx');
after(() => vite.close());
const machineId = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
function fixture(selection, count = 121) {
  return {machineId, streamKey:'mtconnect:M01', throughPosition:'200', acquisitionRevision:'2', ...selection,
    readAsOf:'2026-10-11T00:00:00Z', availableSignals:[{source:'mtconnect',address:'part_count'},{source:'mtconnect',address:'speed'}],
    samples:Array.from({length:count}, (_,i) => ({position:String(i+1), instanceId:'1',sequence:String(i+1),
      observedAtUtc:new Date(Date.parse('2026-10-06T01:00:00Z')+i*1000).toISOString(), signalType:'Numeric',quality:'Good',
      rawValue:String(i),previousPosition:i ? String(i) : null,arithmeticDifference:i ? '1' : null,
      flags:['ContinuityNotEstablished','AttributionNotEstablished',...(i ? [] : ['BaselineOnly'])]}))};
}
test('counter UI pages only retained rows; signal selection is explicit; failed reads remove results', async t => {
  const original = globalThis.fetch; t.after(() => {globalThis.fetch=original;});
  let failed=false;let count=121;let calls=0;let sent;
  globalThis.fetch=async (_, options) => {calls++;sent=JSON.parse(options.body);return failed ? new Response('',{status:503}) : Response.json(fixture(sent,count));};
  const h=await mountInDom(React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000}));t.after(() => h.dispose());
  const dates=h.document.querySelectorAll('input[type=date]'); assert.equal(dates[0].value,dates[1].value);
  await h.changeInput(dates[0],'2026-10-06');await h.changeInput(dates[1],'2026-10-06');await h.submit(dates[0].form);
  assert.equal(sent.address,'part_count');assert.equal(h.document.querySelectorAll('tbody tr').length,50);
  const next=() => [...h.document.querySelectorAll('button')].find(b=>b.textContent==='Next counter page');
  await h.click(next());await h.click(next());assert.equal(h.document.querySelectorAll('tbody tr').length,21);assert.equal(next().disabled,true);assert.equal(calls,1);
  assert.match(h.document.body.textContent,/policy assumption/);assert.match(h.document.body.textContent,/06\/10\/2026/);
  assert.equal(h.document.querySelector('[aria-label="Exact raw counter observations"]').tabIndex,0);
  await h.click([...h.document.querySelectorAll('button')].filter(b=>b.textContent==='Select signal')[1]);
  assert.match(h.document.body.textContent,/Loaded signal: mtconnect \/ part_count/);assert.equal(calls,1);
  count=0;await h.submit(dates[0].form);assert.equal(sent.address,'speed');assert.equal(h.document.querySelectorAll('tbody tr').length,0);
  assert.match(h.document.body.textContent,/does not establish zero production/);assert.equal(next().disabled,true);
  failed=true;await h.submit(dates[0].form);assert.match(h.document.querySelector('[role=alert]').textContent,/503/);assert.equal(h.document.querySelector('table'),null);
});
