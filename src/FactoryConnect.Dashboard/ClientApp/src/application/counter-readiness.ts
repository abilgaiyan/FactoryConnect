import type { CounterReport } from "./machine-counter-report.ts";

export interface CounterReadiness {
  readonly status: "NoSamples" | "EvidenceNeedsReview";
  readonly reasons: readonly string[];
  readonly samples: number;
  readonly nonGood: number;
  readonly decreases: number;
  readonly instanceChanges: number;
  readonly nonIncreasingTimestamps: number;
}

/** Diagnostic only. Never computes accepted production quantity. */
export function counterReadiness(report: CounterReport): CounterReadiness {
  const samples = report.samples;
  const nonGood = samples.filter(s => s.quality !== "Good").length;
  const decreases = samples.filter(s => s.flags.includes("CounterDecreased")).length;
  const instanceChanges = samples.filter(s => s.flags.includes("InstanceChanged")).length;
  const nonIncreasingTimestamps = samples.filter(s => s.flags.includes("NonIncreasingTimestamp")).length;
  const reasons = [
    "Configured MTConnect data item ID is not proven equivalent to the raw source/address.",
    "Counter continuity and shift/operation attribution are not qualified."
  ];
  if (samples.length === 0) reasons.push("No selected counter observations in the requested interval.");
  if (nonGood > 0) reasons.push("Non-good counter observations require review.");
  if (decreases > 0) reasons.push("Counter decreases require reset or discontinuity review.");
  if (instanceChanges > 0) reasons.push("Acquisition instance changes require continuity review.");
  if (nonIncreasingTimestamps > 0) reasons.push("Non-increasing observation timestamps require review.");
  return {
    status: samples.length === 0 ? "NoSamples" : "EvidenceNeedsReview",
    reasons, samples: samples.length, nonGood, decreases, instanceChanges, nonIncreasingTimestamps
  };
}
