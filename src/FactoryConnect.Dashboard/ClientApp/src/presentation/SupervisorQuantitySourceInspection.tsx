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

/** Diagnostic only; passing these checks does not qualify accepted production. */
export function quantityEvidenceDiagnostics(source: SourceRow): string[] {
  const issues: string[] = [];
  const produced = source.partCountIncrement;
  const good = source.goodQuantity;
  const rejected = source.rejectedQuantity;
  if (produced === null) issues.push("Produced increment missing");
  if (good === null) issues.push("Good quantity missing");
  if (rejected === null) issues.push("Rejected quantity missing");
  if (produced !== null && good !== null && rejected !== null &&
      good + rejected !== produced)
    issues.push("Good plus rejected does not equal produced increment");
  if (!source.productionOrderId || !source.partId || !source.operationId)
    issues.push("Production order, part or operation context incomplete");
  return issues;
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
      <p>Source-level consistency: {inventory.sources.filter(source => quantityEvidenceDiagnostics(source).length > 0).length} of {inventory.sourceCount} records have missing or inconsistent quantity/context fields. Even zero flagged records do not establish accepted production.</p>
      {inventory.sources.map(source => <article key={source.evidenceId}>
        <p>Evidence {source.evidenceId} · {source.occurredAtUtc}</p>
        <p>Produced increment: {source.partCountIncrement ?? "Not supplied"} · Good: {source.goodQuantity ?? "Not supplied"} · Rejected: {source.rejectedQuantity ?? "Not supplied"}</p>
        <p>Order {source.productionOrderId ?? "—"} · Part {source.partId ?? "—"} · Operation {source.operationId ?? "—"}</p>
        <p>Contribution positions: {source.contributingPositions.join(", ")}</p>
        <p>Diagnostic: {quantityEvidenceDiagnostics(source).length
          ? quantityEvidenceDiagnostics(source).join("; ")
          : "Basic quantity/context checks passed; acceptance and complete coverage remain unverified"}</p>
      </article>)}
    </>}
  </section>;
}
