export interface RawSignalIdentity { readonly source: string; readonly address: string }
export type CounterCandidateReview =
  | { readonly status: "NoConfiguredCounter"; readonly candidates: readonly RawSignalIdentity[] }
  | { readonly status: "NoSignalsDiscovered"; readonly candidates: readonly RawSignalIdentity[] }
  | { readonly status: "NoExactAddressMatch"; readonly candidates: readonly RawSignalIdentity[] }
  | { readonly status: "AmbiguousCandidates"; readonly candidates: readonly RawSignalIdentity[] }
  | { readonly status: "SingleUnverifiedCandidate"; readonly candidates: readonly RawSignalIdentity[] };

/** Address equality is not a verified MTConnect data-item mapping. */
export function reviewCounterCandidates(counterDataItemId: string | null,
  signals: readonly RawSignalIdentity[]): CounterCandidateReview {
  if (!counterDataItemId) return { status: "NoConfiguredCounter", candidates: [] };
  if (signals.length === 0) return { status: "NoSignalsDiscovered", candidates: [] };
  const candidates = signals.filter(s => s.address === counterDataItemId);
  if (candidates.length === 0) return { status: "NoExactAddressMatch", candidates };
  return { status: candidates.length === 1 ? "SingleUnverifiedCandidate" : "AmbiguousCandidates", candidates };
}
