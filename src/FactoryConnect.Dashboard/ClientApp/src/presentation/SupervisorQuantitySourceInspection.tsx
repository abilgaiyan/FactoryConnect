import { useEffect, useState } from "react";
import type { ShiftPerformanceMachine, ShiftPerformanceShift } from "./shift-performance-model.ts";

interface SourceRow {
  evidenceId: string;
  occurredAtUtc: string;
  partCountIncrement: number | null;
  goodQuantity: number | null;
  rejectedQuantity: number | null;
  productionOrderId: string | null;
  partId: string | null;
  operationId: string | null;
  contributingPositions: string[];
}
interface Inventory {
  classification: string;
  sourceCount: number;
  sources: SourceRow[];
}

export function SupervisorQuantitySourceInspection({ machine, shift }: {
  readonly machine: ShiftPerformanceMachine;
  readonly shift: ShiftPerformanceShift;
}) {
  const [refresh, setRefresh] = useState(0);
  const [status, setStatus] = useState("Loading historical quantity sources…");
  const [inventory, setInventory] = useState<Inventory | null>(null);
  const revision = shift.sourceRevision;
  const position = revision?.position;
  useEffect(() => {
    if (!revision || !position) {
      setStatus("No reporting source revision. Historical quantity sources cannot be inspected.");
      setInventory(null);
      return;
    }
    const controller = new AbortController();
    const query = new URLSearchParams({
      siteId: machine.siteId,
      assignmentId: shift.shift.shiftScheduleAssignmentId,
      shiftId: shift.shift.shiftId,
      shiftStartsAtUtc: shift.shift.startsAtUtc,
      shiftEndsAtUtc: shift.shift.endsAtUtc,
      aggregationProcessorId: revision.processorId,
      streamKey: revision.streamKey,
      position: String(position)
    });
    setInventory(null);
    setStatus("Loading historical quantity sources…");
    void fetch(`/api/supervisor/v1/machines/${encodeURIComponent(machine.machineId)}/quantity-source-cut?${query}`,
      { signal: controller.signal }).then(async response => {
        if (!response.ok) throw new Error(`Historical source inventory unavailable (HTTP ${response.status})`);
        const body: unknown = await response.json();
        if (!body || typeof body !== "object" || !("classification" in body) ||
          body.classification !== "HistoricalMetricInputSourceInventoryNotQualifiedActual" ||
          !("sources" in body) || !Array.isArray(body.sources) ||
          !("sourceCount" in body) || body.sourceCount !== body.sources.length)
          throw new Error("Historical source inventory response invalid");
        if (!controller.signal.aborted) {
          setInventory(body as Inventory);
          setStatus("");
        }
      }).catch(error => {
        if (!controller.signal.aborted) setStatus(String(error));
      });
    return () => controller.abort();
  }, [machine.machineId, machine.siteId, shift, refresh, position]);
  return <section className="supervisor-detail" aria-label="Historical production quantity source inventory">
    <h3>Historical quantity source inspection</h3>
    <p>Read-only source facts at the reported aggregation revision. This is not a verified production total, complete coverage, or target achievement.</p>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh quantity source inventory</button>
    {status && <p role="status">{status}</p>}
    {inventory && <>
      <p>{inventory.sourceCount} historical source record(s). No accepted-quantity total is calculated.</p>
      {inventory.sources.map(source => <article key={source.evidenceId}>
        <p>Evidence {source.evidenceId} · {source.occurredAtUtc}</p>
        <p>Produced increment: {source.partCountIncrement ?? "Not supplied"} · Good: {source.goodQuantity ?? "Not supplied"} · Rejected: {source.rejectedQuantity ?? "Not supplied"}</p>
        <p>Order {source.productionOrderId ?? "—"} · Part {source.partId ?? "—"} · Operation {source.operationId ?? "—"}</p>
        <p>Contribution positions: {source.contributingPositions.join(", ")}</p>
      </article>)}
    </>}
  </section>;
}
