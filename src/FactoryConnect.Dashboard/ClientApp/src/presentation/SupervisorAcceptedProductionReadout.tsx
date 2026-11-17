import { useEffect, useState } from "react";
import type { ShiftPerformanceMachine, ShiftPerformanceShift } from "./shift-performance-model.ts";

type AcceptedPublication = {
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
  coverage: "Complete";
  authorityReference: string;
  publishedAtUtc: string;
};

function validPublication(value: unknown, machine: ShiftPerformanceMachine, shift: ShiftPerformanceShift): value is AcceptedPublication {
  if (!value || typeof value !== "object") return false;
  const p = value as Partial<AcceptedPublication>;
  return p.classification === "AcceptedShiftProduction" &&
    typeof p.publicationId === "string" && /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i.test(p.publicationId) &&
    p.machineId === machine.machineId &&
    p.shiftScheduleAssignmentId === shift.shift.shiftScheduleAssignmentId &&
    p.shiftId === shift.shift.shiftId &&
    p.shiftStartsAtUtc === shift.shift.startsAtUtc &&
    p.shiftEndsAtUtc === shift.shift.endsAtUtc &&
    p.coverage === "Complete" &&
    typeof p.authorityReference === "string" && p.authorityReference.trim().length > 0 &&
    typeof p.quantityUnit === "string" && p.quantityUnit.trim().length > 0 &&
    typeof p.acceptedProducedQuantity === "number" &&
    Number.isSafeInteger(p.acceptedProducedQuantity) && p.acceptedProducedQuantity >= 0;
}

/** An accepted publication is separate from historical source inventory and local declarations. */
export function SupervisorAcceptedProductionReadout({ machine, shift }: {
  readonly machine: ShiftPerformanceMachine;
  readonly shift: ShiftPerformanceShift;
}) {
  const [refresh, setRefresh] = useState(0);
  const [state, setState] = useState<
    { kind: "loading" } | { kind: "unavailable"; reason: string } |
    { kind: "error"; message: string } | { kind: "ready"; publication: AcceptedPublication }
  >({ kind: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    const query = new URLSearchParams({
      assignmentId: shift.shift.shiftScheduleAssignmentId,
      shiftId: shift.shift.shiftId,
      shiftStartsAtUtc: shift.shift.startsAtUtc,
      shiftEndsAtUtc: shift.shift.endsAtUtc
    });
    setState({ kind: "loading" });
    void fetch(`/api/supervisor/v1/machines/${encodeURIComponent(machine.machineId)}/accepted-shift-production?${query}`,
      { signal: controller.signal }).then(async response => {
        if (!response.ok) throw new Error(`Accepted production read failed (HTTP ${response.status})`);
        const body: unknown = await response.json();
        if (body && typeof body === "object" && "classification" in body &&
          body.classification === "AcceptedProductionUnavailable" &&
          "reason" in body && typeof body.reason === "string") {
          if (!controller.signal.aborted) setState({ kind: "unavailable", reason: body.reason });
          return;
        }
        if (!validPublication(body, machine, shift))
          throw new Error("Accepted production response invalid or mismatched to selected shift");
        if (!controller.signal.aborted) setState({ kind: "ready", publication: body });
      }).catch(error => {
        if (!controller.signal.aborted) setState({ kind: "error", message: String(error) });
      });
    return () => controller.abort();
  }, [machine.machineId, shift.shift.shiftScheduleAssignmentId, shift.shift.shiftId,
    shift.shift.startsAtUtc, shift.shift.endsAtUtc, refresh]);
  return <section aria-label="Accepted actual production" className="supervisor-detail">
    <h3>Accepted actual production · SQL publication</h3>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh accepted production</button>
    {state.kind === "loading" && <p role="status">Loading accepted production publication…</p>}
    {state.kind === "unavailable" && <p role="status">Accepted actual: unavailable ({state.reason}). This does not mean zero production.</p>}
    {state.kind === "error" && <p role="alert">{state.message}. Accepted actual remains unknown.</p>}
    {state.kind === "ready" && <>
      <p><strong>{state.publication.acceptedProducedQuantity} {state.publication.quantityUnit}</strong> · Accepted actual for this exact shift</p>
      <p>Order {state.publication.productionOrder ?? "—"} · Part {state.publication.partNumber ?? "—"} · Operation {state.publication.operation ?? "—"}</p>
      <p>Publication {state.publication.publicationId} · Authority {state.publication.authorityReference} · Published {state.publication.publishedAtUtc}</p>
      <p>Complete accepted publication is available. Target achievement and OEE remain separate qualification calculations.</p>
    </>}
  </section>;
}
