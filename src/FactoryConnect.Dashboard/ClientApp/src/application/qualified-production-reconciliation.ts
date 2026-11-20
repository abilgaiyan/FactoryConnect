/**
 * Pre-publication reconciliation of independently qualified production contributions.
 * This does not qualify evidence, authorize publication, or calculate OEE.
 * Population keys must be assigned by an independent qualification authority.
 */
export type QualifiedProductionContribution = {
  evidenceId: string;
  populationKey: string;
  authorityReference: string;
  machineId: string;
  shiftScheduleAssignmentId: string;
  shiftId: string;
  shiftStartsAtUtc: string;
  shiftEndsAtUtc: string;
  quantityUnit: string;
  productionOrder: string | null;
  partNumber: string | null;
  operation: string | null;
  produced: number;
  good: number;
  rejected: number;
  qualification: "Qualified";
};

export type ReconciliationScope = Pick<QualifiedProductionContribution,
  "machineId" | "shiftScheduleAssignmentId" | "shiftId" |
  "shiftStartsAtUtc" | "shiftEndsAtUtc" | "quantityUnit" |
  "productionOrder" | "partNumber" | "operation">;

export type ReconciliationCandidate =
  | { state: "candidate"; produced: number; good: number; rejected: number;
      evidenceIds: string[]; authorityReferences: string[] }
  | { state: "held"; reason: string };

function validUtc(value: string): boolean {
  return typeof value === "string" &&
    /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$/.test(value) &&
    Number.isFinite(Date.parse(value));
}

export function reconcileQualifiedProduction(
  scope: ReconciliationScope, contributions: readonly QualifiedProductionContribution[]
): ReconciliationCandidate {
  if (!scope.machineId?.trim() || !scope.shiftScheduleAssignmentId?.trim() ||
      !scope.shiftId?.trim() || !scope.quantityUnit?.trim() ||
      !validUtc(scope.shiftStartsAtUtc) ||
      !validUtc(scope.shiftEndsAtUtc) ||
      Date.parse(scope.shiftEndsAtUtc) <= Date.parse(scope.shiftStartsAtUtc))
    return { state: "held", reason: "InvalidShiftScope" };
  if (contributions.length === 0)
    return { state: "held", reason: "NoQualifiedContributions" };
  const populations = new Set<string>();
  const evidenceIds = new Set<string>();
  const authorities = new Set<string>();
  let produced = 0, good = 0, rejected = 0;
  for (const item of contributions) {
    if (item.qualification !== "Qualified" || !item.authorityReference?.trim())
      return { state: "held", reason: "QualificationMissing" };
    if (item.machineId !== scope.machineId ||
        item.shiftScheduleAssignmentId !== scope.shiftScheduleAssignmentId ||
        item.shiftId !== scope.shiftId ||
        item.shiftStartsAtUtc !== scope.shiftStartsAtUtc ||
        item.shiftEndsAtUtc !== scope.shiftEndsAtUtc ||
        item.quantityUnit !== scope.quantityUnit)
      return { state: "held", reason: "ScopeMismatch" };
    if (item.productionOrder !== scope.productionOrder ||
        item.partNumber !== scope.partNumber ||
        item.operation !== scope.operation)
      return { state: "held", reason: "ProductionContextMismatch" };
    if (!item.evidenceId?.trim() || !item.populationKey?.trim())
      return { state: "held", reason: "ProvenanceMissing" };
    if (evidenceIds.has(item.evidenceId))
      return { state: "held", reason: "DuplicateEvidence" };
    if (populations.has(item.populationKey))
      return { state: "held", reason: "PopulationOverlap" };
    if (![item.produced, item.good, item.rejected].every(
      n => Number.isSafeInteger(n) && n >= 0) ||
      !Number.isSafeInteger(item.good + item.rejected) ||
      item.good + item.rejected !== item.produced)
      return { state: "held", reason: "QuantityInconsistent" };
    produced += item.produced; good += item.good; rejected += item.rejected;
    if (![produced, good, rejected].every(Number.isSafeInteger))
      return { state: "held", reason: "QuantityOverflow" };
    populations.add(item.populationKey);
    evidenceIds.add(item.evidenceId);
    authorities.add(item.authorityReference);
  }
  return { state: "candidate", produced, good, rejected,
    evidenceIds: [...evidenceIds].sort(), authorityReferences: [...authorities].sort() };
}
