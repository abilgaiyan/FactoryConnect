import { useEffect, useState } from "react";
import { calculateAcceptedTargetAttainment, type AcceptedShiftProduction, type ShiftPlanningTarget } from "../application/accepted-shift-target-attainment.ts";
import type { ShiftPerformanceMachine, ShiftPerformanceShift } from "./shift-performance-model.ts";

type Revision = ShiftPlanningTarget & { targetRevisionId: string; revisionNumber: number };
type ReadState = { kind: "loading" } | { kind: "error"; message: string } |
  { kind: "ready"; result: ReturnType<typeof calculateAcceptedTargetAttainment>; revision: number | null };

function isRevision(value: unknown): value is Revision {
  if (!value || typeof value !== "object") return false;
  const r = value as Partial<Revision>;
  return typeof r.targetRevisionId === "string" && r.targetRevisionId.length > 0 &&
    Number.isSafeInteger(r.revisionNumber) && (r.revisionNumber ?? 0) > 0 &&
    typeof r.machineId === "string" && typeof r.shiftScheduleAssignmentId === "string" &&
    typeof r.shiftId === "string" && typeof r.shiftStartsAtUtc === "string" &&
    typeof r.shiftEndsAtUtc === "string" && typeof r.quantityUnit === "string" &&
    typeof r.targetQuantity === "number" && Number.isFinite(r.targetQuantity) &&
    (r.productionOrder === null || typeof r.productionOrder === "string") &&
    (r.partNumber === null || typeof r.partNumber === "string") &&
    (r.operation === null || typeof r.operation === "string");
}
function isAccepted(value: unknown): value is AcceptedShiftProduction {
  if (!value || typeof value !== "object") return false;
  const a = value as Partial<AcceptedShiftProduction>;
  return a.classification === "AcceptedShiftProduction" &&
    typeof a.publicationId === "string" && a.publicationId.length > 0 &&
    typeof a.machineId === "string" && typeof a.shiftScheduleAssignmentId === "string" &&
    typeof a.shiftId === "string" && typeof a.shiftStartsAtUtc === "string" &&
    typeof a.shiftEndsAtUtc === "string" && typeof a.quantityUnit === "string" &&
    a.coverage === "Complete" && typeof a.acceptedProducedQuantity === "number" &&
    Number.isSafeInteger(a.acceptedProducedQuantity) && a.acceptedProducedQuantity >= 0 &&
    (a.productionOrder === null || typeof a.productionOrder === "string") &&
    (a.partNumber === null || typeof a.partNumber === "string") &&
    (a.operation === null || typeof a.operation === "string");
}

export function SupervisorTargetAchievement({ machine, shift }: {
  readonly machine: ShiftPerformanceMachine; readonly shift: ShiftPerformanceShift;
}) {
  const [refresh, setRefresh] = useState(0);
  const [state, setState] = useState<ReadState>({ kind: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    const scope = shift.shift;
    const base = `/api/supervisor/v1/machines/${encodeURIComponent(machine.machineId)}`;
    const targetQuery = new URLSearchParams({
      assignmentId: scope.shiftScheduleAssignmentId, shiftStartsAtUtc: scope.startsAtUtc,
      shiftEndsAtUtc: scope.endsAtUtc
    });
    const actualQuery = new URLSearchParams({
      assignmentId: scope.shiftScheduleAssignmentId, shiftId: scope.shiftId,
      shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc
    });
    setState({ kind: "loading" });
    void Promise.all([
      fetch(`${base}/shift-targets?${targetQuery}`, { signal: controller.signal }),
      fetch(`${base}/accepted-shift-production?${actualQuery}`, { signal: controller.signal })
    ]).then(async ([targetResponse, actualResponse]) => {
      if (!targetResponse.ok || !actualResponse.ok)
        throw new Error(`Planning or accepted-production SQL read unavailable (HTTP ${targetResponse.status}/${actualResponse.status})`);
      const targets: unknown = await targetResponse.json();
      const actual: unknown = await actualResponse.json();
      if (!Array.isArray(targets) || !targets.every(isRevision))
        throw new Error("Invalid SQL target revision history");
      if (targets.some(item => item.machineId !== machine.machineId ||
        item.shiftScheduleAssignmentId !== scope.shiftScheduleAssignmentId ||
        item.shiftId !== scope.shiftId || item.shiftStartsAtUtc !== scope.startsAtUtc ||
        item.shiftEndsAtUtc !== scope.endsAtUtc))
        throw new Error("Target revision history does not match selected occurrence");
      const revisions = targets as Revision[];
      const numbers = new Set(revisions.map(item => item.revisionNumber));
      if (numbers.size !== revisions.length)
        throw new Error("Conflicting target revisions");
      const head = revisions.reduce<Revision | null>((latest, item) =>
        !latest || item.revisionNumber > latest.revisionNumber ? item : latest, null);
      let publication: AcceptedShiftProduction | null = null;
      if (actual && typeof actual === "object" && "classification" in actual &&
        actual.classification === "AcceptedProductionUnavailable" &&
        "reason" in actual && actual.reason === "NoQualifiedPublicationForShift") {
        publication = null;
      } else if (isAccepted(actual)) {
        if (actual.machineId !== machine.machineId ||
            actual.shiftScheduleAssignmentId !== scope.shiftScheduleAssignmentId ||
            actual.shiftId !== scope.shiftId ||
            actual.shiftStartsAtUtc !== scope.startsAtUtc ||
            actual.shiftEndsAtUtc !== scope.endsAtUtc)
          throw new Error("Accepted production publication does not match selected shift occurrence");
        publication = actual;
      } else {
        throw new Error("Invalid accepted-production publication response");
      }
      const result = calculateAcceptedTargetAttainment(head, publication);
      if (!controller.signal.aborted)
        setState({ kind: "ready", result, revision: head?.revisionNumber ?? null });
    }).catch(error => {
      if (!controller.signal.aborted) setState({ kind: "error", message: String(error) });
    });
    return () => controller.abort();
  }, [machine.machineId, shift.shift.shiftScheduleAssignmentId, shift.shift.shiftId,
    shift.shift.startsAtUtc, shift.shift.endsAtUtc, refresh]);
  return <section className="supervisor-detail" aria-label="Shift target achievement">
    <h3>Shift target achievement</h3>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh target achievement</button>
    {state.kind === "loading" && <p role="status">Loading target and accepted actual…</p>}
    {state.kind === "error" && <p role="alert">{state.message}. Achievement unavailable; not zero.</p>}
    {state.kind === "ready" && (state.result.state === "calculated"
      ? <>
          <p><strong>{state.result.percent.toFixed(1)}% achieved</strong> · {state.result.acceptedActual} accepted / {state.result.target} planned</p>
          <p>Target revision {state.revision} · Accepted publication {state.result.publicationId}</p>
          <p>Planning achievement is separate from qualified OEE Performance and OEE.</p>
        </>
      : <p role="status">Achievement unavailable: {state.result.reason}. Missing evidence is not zero.</p>)}
  </section>;
}
