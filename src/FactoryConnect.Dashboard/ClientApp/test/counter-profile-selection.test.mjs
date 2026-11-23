import { test } from 'node:test';
import assert from 'node:assert/strict';
import { profileSignalSelectionStatus } from '../src/application/counter-profile-selection.ts';

const signal = {source:'mtconnect', address:'counter'};
const report = (disposition, candidates, source='mtconnect', address='counter') => ({
  source, address, identityAssessment: {
    disposition, candidates, identityVerified:false, productionQuantityAdmitted:false,
  },
});

test('selected exact candidate remains explicitly unverified', () => {
  assert.equal(profileSignalSelectionStatus(report('SingleUnverifiedCandidate',[signal])), 'SelectedCandidate');
  assert.equal(profileSignalSelectionStatus(report('SingleUnverifiedCandidate',[signal],'other','counter')), 'SelectedSignalDiffers');
});

test('missing and ambiguous candidates cannot appear as selected verified identity', () => {
  assert.equal(profileSignalSelectionStatus(report('NoExactAddressCandidate',[])), 'NoExactCandidate');
  assert.equal(profileSignalSelectionStatus(report('NoDiscoveredSignals',[])), 'NoExactCandidate');
  assert.equal(profileSignalSelectionStatus(report('NoConfiguredDataItem',[])), 'NoConfiguredCandidate');
  assert.equal(profileSignalSelectionStatus(report('AmbiguousExactAddressCandidates',
    [signal,{source:'other',address:'counter'}])), 'AmbiguousCandidates');
});
