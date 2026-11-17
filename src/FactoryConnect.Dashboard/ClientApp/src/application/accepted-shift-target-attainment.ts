/**
 * Target attainment is only available for a server-published accepted production fact.
 * Historical source cuts, local declarations and locally approved reviews are not inputs.
 */
export type AcceptedShiftProduction = {
  classification: "AcceptedShiftProduction";
  publicationId: string;
  machineId: string;
  shiftScheduleAssignmentId: string;
  shiftId: string;
  shiftStartsAtUtc: string;
  shiftEndsAtUtc: string;
  productionOrder: string | null;
  partNumber: string | null;
  operation: string | null;
  quantityUnit: string;
  acceptedProducedQuantity: number;
  coverage: "Complete" | "Incomplete" | "Conflicted";
};

export type ShiftPlanningTarget = {
  machineId: string;
  shiftScheduleAssignmentId: string;
  shiftId: string;
  shiftStartsAtUtc: string;
  shiftEndsAtUtc: string;
  productionOrder: string | null;
  partNumber: string | null;
  operation: string | null;
  quantityUnit: string;
  targetQuantity: number;
};

export type TargetAttainment =
  | { state: "calculated"; acceptedActual: number; target: number; percent: number; publicationId: string }
  | { state: "unavailable"; reason: string };

export function calculateAcceptedTargetAttainment(
  target: ShiftPlanningTarget | null,
  accepted: AcceptedShiftProduction | null
): TargetAttainment {
  if (!target) return { state: "unavailable", reason: "TargetMissing" };
  if (!accepted) return { state: "unavailable", reason: "AcceptedProductionMissing" };
  if (accepted.classification !== "AcceptedShiftProduction" || !accepted.publicationId?.trim())
    return { state: "unavailable", reason: "AcceptedPublicationMissing" };
  if (accepted.coverage !== "Complete")
    return { state: "unavailable", reason: "ProductionCoverageNotComplete" };
  if (target.machineId !== accepted.machineId ||
      target.shiftScheduleAssignmentId !== accepted.shiftScheduleAssignmentId ||
      target.shiftId !== accepted.shiftId ||
      target.shiftStartsAtUtc !== accepted.shiftStartsAtUtc ||
      target.shiftEndsAtUtc !== accepted.shiftEndsAtUtc)
    return { state: "unavailable", reason: "ShiftOccurrenceMismatch" };
  if (target.productionOrder !== accepted.productionOrder ||
      target.partNumber !== accepted.partNumber ||
      target.operation !== accepted.operation)
    return { state: "unavailable", reason: "ProductionContextMismatch" };
  if (!target.quantityUnit.trim() ||
      target.quantityUnit.trim().toLocaleLowerCase() !== accepted.quantityUnit.trim().toLocaleLowerCase())
    return { state: "unavailable", reason: "QuantityUnitMismatch" };
  if (!Number.isSafeInteger(accepted.acceptedProducedQuantity) || accepted.acceptedProducedQuantity < 0)
    return { state: "unavailable", reason: "AcceptedQuantityInvalid" };
  if (!Number.isFinite(target.targetQuantity) || target.targetQuantity <= 0)
    return { state: "unavailable", reason: "TargetNotPositive" };
  const percent = accepted.acceptedProducedQuantity / target.targetQuantity * 100;
  if (!Number.isFinite(percent))
    return { state: "unavailable", reason: "AttainmentOverflow" };
  return { state: "calculated", acceptedActual: accepted.acceptedProducedQuantity,
    target: target.targetQuantity, percent, publicationId: accepted.publicationId };
}
