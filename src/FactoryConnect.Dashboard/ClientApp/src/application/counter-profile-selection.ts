import type { CounterReport } from './machine-counter-report.ts';

export type ProfileSignalSelectionStatus =
  'NoConfiguredCandidate' | 'NoExactCandidate' | 'AmbiguousCandidates' |
  'SelectedCandidate' | 'SelectedSignalDiffers';

export function profileSignalSelectionStatus(report: CounterReport): ProfileSignalSelectionStatus {
  const assessment = report.identityAssessment;
  if (!assessment || assessment.disposition === 'NoConfiguredDataItem') return 'NoConfiguredCandidate';
  if (assessment.disposition === 'NoDiscoveredSignals' ||
      assessment.disposition === 'NoExactAddressCandidate') return 'NoExactCandidate';
  if (assessment.disposition === 'AmbiguousExactAddressCandidates') return 'AmbiguousCandidates';
  return assessment.candidates.some(candidate =>
    candidate.source === report.source && candidate.address === report.address)
    ? 'SelectedCandidate' : 'SelectedSignalDiffers';
}
