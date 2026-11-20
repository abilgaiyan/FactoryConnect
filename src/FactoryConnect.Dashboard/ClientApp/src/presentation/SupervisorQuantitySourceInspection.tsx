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

/** Reject malformed inventory before diagnostics or CSV export can imply trustworthy source rows. */
export function validQuantitySourceInventory(value: unknown): value is Inventory {
  if (!value || typeof value !== "object") return false;
  const inventory = value as Partial<Inventory>;
  if (inventory.classification !== "HistoricalMetricInputSourceInventoryNotQualifiedActual" ||
      !Array.isArray(inventory.sources) || !Number.isSafeInteger(inventory.sourceCount) ||
      inventory.sourceCount !== inventory.sources.length) return false;
  return inventory.sources.every((item: unknown) => {
    if (!item || typeof item !== "object") return false;
    const source = item as Partial<SourceRow>;
    const quantity = (v: unknown) => v === null || (typeof v === "number" && Number.isSafeInteger(v) && v >= 0);
    const context = (v: unknown) => v === null || typeof v === "string";
    return typeof source.evidenceId === "string" && source.evidenceId.length > 0 &&
      typeof source.occurredAtUtc === "string" && Number.isFinite(Date.parse(source.occurredAtUtc)) &&
      quantity(source.partCountIncrement) && quantity(source.goodQuantity) && quantity(source.rejectedQuantity) &&
      context(source.productionOrderId) && context(source.partId) && context(source.operationId) &&
      Array.isArray(source.contributingPositions) &&
      source.contributingPositions.every(position => typeof position === "string" && /^\d+$/.test(position));
  });
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

/** Actionable source-level gaps; never an accepted-production qualification. */
export function quantityEvidenceGapCounts(sources: readonly SourceRow[]): { issue: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const source of sources) {
    for (const issue of quantityEvidenceDiagnostics(source)) {
      counts.set(issue, (counts.get(issue) ?? 0) + 1);
    }
  }
  return [...counts].map(([issue, count]) => ({ issue, count }));
}

function exportQuantityEvidenceCsv(machine: ShiftPerformanceMachine, shift: ShiftPerformanceShift, inventory: Inventory) {
  const cell = (value: string) => {
    const safe = /^\s*[=+@-]/.test(value) ? "'" + value : value;
    return '"' + safe.replace(/"/g, '""') + '"';
  };
  const headers = ["Classification", "Machine ID", "Site ID", "Shift ID", "Shift assignment",
    "Shift start UTC", "Shift end UTC", "Processor", "Stream", "Checkpoint position",
    "Evidence ID", "Occurred UTC", "Produced increment", "Good quantity", "Rejected quantity",
    "Production order", "Part", "Operation", "Contributing positions", "Source diagnostics"];
  const context = [inventory.classification, machine.machineId, machine.siteId, shift.shift.shiftId,
    shift.shift.shiftScheduleAssignmentId, shift.shift.startsAtUtc, shift.shift.endsAtUtc,
    shift.sourceRevision?.processorId ?? "", shift.sourceRevision?.streamKey ?? "",
    String(shift.sourceRevision?.position ?? "")];
  const rows = inventory.sources.map(source => [...context, source.evidenceId, source.occurredAtUtc,
    source.partCountIncrement === null ? "" : String(source.partCountIncrement),
    source.goodQuantity === null ? "" : String(source.goodQuantity),
    source.rejectedQuantity === null ? "" : String(source.rejectedQuantity),
    source.productionOrderId ?? "", source.partId ?? "", source.operationId ?? "",
    source.contributingPositions.join(";"), quantityEvidenceDiagnostics(source).join(";")]);
  const csv = [
    ["Notice", "Historical source inventory only; not qualified actual production or target attainment"],
    headers, ...rows
  ].map(row => row.map(cell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob(["\uFEFF", csv], { type: "text/csv;charset=utf-8" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = "supervisor-quantity-source-evidence.csv";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
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
        if (!validQuantitySourceInventory(body))
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
      <button type="button" onClick={() => exportQuantityEvidenceCsv(machine, shift, inventory)}>Export historical source evidence (CSV)</button>
      <p>{inventory.sourceCount} historical source record(s). No accepted-quantity total is calculated.</p>
      <p>Source-level consistency: {inventory.sources.filter(source => quantityEvidenceDiagnostics(source).length > 0).length} of {inventory.sourceCount} records have missing or inconsistent quantity/context fields. Even zero flagged records do not establish accepted production.</p>
      {quantityEvidenceGapCounts(inventory.sources).length > 0 && <section aria-label="Quantity evidence gaps by reason">
        <h4>Evidence gaps to investigate</h4>
        <ul>{quantityEvidenceGapCounts(inventory.sources).map(({ issue, count }) =>
          <li key={issue}>{issue}: {count} source record(s)</li>)}</ul>
        <p>Resolve these source inconsistencies before assessing authority and shift coverage. This list is diagnostic, not a production approval.</p>
      </section>}
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
