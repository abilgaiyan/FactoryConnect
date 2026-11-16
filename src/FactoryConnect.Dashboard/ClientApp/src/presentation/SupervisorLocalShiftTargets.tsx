import { useEffect, useState, type FormEvent } from "react";

type Scope = {
  machineId: string; shiftScheduleAssignmentId: string; shiftId: string;
  startsAtUtc: string; endsAtUtc: string;
};
type Revision = {
  targetRevisionId: string; machineId: string; shiftScheduleAssignmentId: string;
  shiftId: string; shiftStartsAtUtc: string; shiftEndsAtUtc: string;
  revisionNumber: number; targetQuantity: number; quantityUnit: string;
  productionOrder: string | null; partNumber: string | null;
  operation: string | null; revisionReason: string | null;
  enteredBy: string; recordedAtUtc: string;
};

export function buildShiftTargetHistoryCsv(scope: Scope, revisions: readonly Revision[]): string {
  const cell = (value: string | number) => {
    const text = String(value);
    const safe = /^\s*[=+@-]/.test(text) ? "'" + text : text;
    return '"' + safe.replace(/"/g, '""') + '"';
  };
  const columns = ["Machine ID", "Shift assignment", "Shift ID", "Shift start UTC", "Shift end UTC",
    "Revision", "Target quantity", "Unit", "Production order", "Part number", "Operation",
    "Revision reason", "Entered by", "Recorded UTC", "Evidence classification"];
  const rows = revisions.map(item => [
    scope.machineId, scope.shiftScheduleAssignmentId, scope.shiftId, scope.startsAtUtc, scope.endsAtUtc,
    item.revisionNumber, item.targetQuantity, item.quantityUnit, item.productionOrder ?? "",
    item.partNumber ?? "", item.operation ?? "", item.revisionReason ?? "",
    item.enteredBy, item.recordedAtUtc, "Planning target only; not achieved production or OEE evidence"
  ]);
  return [columns, ...rows].map(row => row.map(cell).join(",")).join("\r\n");
}

export function SupervisorLocalShiftTargets({ scope }: { readonly scope: Scope }) {
  const [history, setHistory] = useState<Revision[]>([]);
  const [quantity, setQuantity] = useState("");
  const [unit, setUnit] = useState("pieces");
  const [order, setOrder] = useState("");
  const [part, setPart] = useState("");
  const [operation, setOperation] = useState("");
  const [reason, setReason] = useState("");
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [pendingPayload, setPendingPayload] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(true);
  const [status, setStatus] = useState("");
  const [loadError, setLoadError] = useState("");
  const [refreshKey, setRefreshKey] = useState(0);
  const url = `/api/supervisor/v1/machines/${encodeURIComponent(scope.machineId)}/shift-targets`;
  const query = new URLSearchParams({
    assignmentId: scope.shiftScheduleAssignmentId,
    shiftStartsAtUtc: scope.startsAtUtc,
    shiftEndsAtUtc: scope.endsAtUtc
  });
  useEffect(() => {
    const controller = new AbortController();
    setHistory([]); setLoadError(""); setLoading(true);
    void fetch(`${url}?${query}`, { signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error(`Shift targets unavailable (HTTP ${response.status})`);
      const body: unknown = await response.json();
      if (!Array.isArray(body)) throw new Error("Unexpected shift target history");
      if (!controller.signal.aborted) {
        setHistory(body as Revision[]);
        if (pendingId && body.some((item: Revision) => item.targetRevisionId === pendingId)) {
          setPendingId(null); setPendingPayload(null);
          setQuantity(""); setReason("");
          setStatus("Previously uncertain save confirmed in SQL revision history.");
        }
      }
    }).catch(error => {
      if (!controller.signal.aborted) setLoadError(String(error));
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [url, scope.shiftScheduleAssignmentId, scope.startsAtUtc, scope.endsAtUtc, refreshKey]);

  function exportHistory() {
    if (loading || loadError || !history.length) return;
    const csv = buildShiftTargetHistoryCsv(scope, history);
    const href = URL.createObjectURL(new Blob(["\uFEFF", csv], { type: "text/csv;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = "supervisor-shift-target-history.csv";
    document.body.appendChild(anchor);
    anchor.click(); anchor.remove();
    URL.revokeObjectURL(href);
  }

  const head = history.at(-1);
  async function save(event: FormEvent) {
    event.preventDefault();
    if (saving || loading || loadError) return;
    if (!/^(?:0|[1-9]\d*)(?:\.\d{1,3})?$/.test(quantity) ||
        !Number.isFinite(Number(quantity)) || Number(quantity) >= 1e15) {
      setStatus("Enter a nonnegative target with at most three decimal places."); return;
    }
    if (!unit.trim() || (head && !reason.trim())) {
      setStatus("Provide a unit and a reason for every revision."); return;
    }
    const id = pendingId ?? crypto.randomUUID();
    const payload = pendingPayload ?? JSON.stringify({
      targetRevisionId: id, shiftScheduleAssignmentId: scope.shiftScheduleAssignmentId,
      shiftId: scope.shiftId, shiftStartsAtUtc: scope.startsAtUtc,
      shiftEndsAtUtc: scope.endsAtUtc, revisionNumber: (head?.revisionNumber ?? 0) + 1,
      expectedHeadRevisionId: head?.targetRevisionId ?? null,
      targetQuantity: Number(quantity), quantityUnit: unit.trim(),
      productionOrder: order.trim() || null, partNumber: part.trim() || null,
      operation: operation.trim() || null, revisionReason: reason.trim() || null
    });
    setPendingId(id); setPendingPayload(payload); setSaving(true); setStatus("");
    try {
      const response = await fetch(url, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: payload
      });
      if (!response.ok) throw new Error(`Save not confirmed (HTTP ${response.status}). Check history before retrying.`);
      setPendingId(null); setPendingPayload(null); setQuantity(""); setReason("");
      setStatus("Shift target revision saved. This is planning only, not accepted production or OEE evidence.");
      setRefreshKey(value => value + 1);
    } catch (error) {
      setStatus(String(error));
    } finally {
      setSaving(false);
    }
  }
  return <section aria-label="Durable shift planning targets" className="supervisor-editor">
    <h2>Shift targets · SQL planning history</h2>
    <p>Exact machine and shift occurrence: {scope.shiftId} · {scope.startsAtUtc} – {scope.endsAtUtc} UTC.</p>
    <p>Targets are whole-shift goals. Revisions replace the previous goal; they are never added together or treated as achieved production.</p>
    <button type="button" onClick={() => setRefreshKey(value => value + 1)}>Reload target history</button>
    {loadError && <p role="alert">{loadError}. Saving is disabled until a successful read.</p>}
    <h3>Revision history</h3>
    <button type="button" disabled={loading || Boolean(loadError) || !history.length} onClick={exportHistory}>Export target history (CSV)</button>
    <p>CSV exports planning revisions only, not achieved production or qualified OEE.</p>
    {loading && <p>Loading SQL target history…</p>}
    {!loading && !loadError && !history.length && <p>Target not provided · TargetMissing</p>}
    {history.map(item => <article className="supervisor-detail" key={item.targetRevisionId}>
      <h4>Revision {item.revisionNumber} · {item.targetQuantity} {item.quantityUnit}</h4>
      <p>Recorded {item.recordedAtUtc} · Entered by {item.enteredBy}</p>
      <p>Order {item.productionOrder || "—"} · Part {item.partNumber || "—"} · Operation {item.operation || "—"}</p>
      <p>Revision reason: {item.revisionReason || "Initial target"}</p>
    </article>)}
    <form onSubmit={save}>
      <fieldset disabled={saving || loading || Boolean(loadError)}>
        <legend>{head ? "Revise whole-shift target" : "Set first whole-shift target"}</legend>
        <label>Target quantity<input required inputMode="decimal" value={quantity} onChange={event => setQuantity(event.target.value)} /></label>
        <label>Unit<input required maxLength={50} value={unit} onChange={event => setUnit(event.target.value)} /></label>
        <label>Production order<input maxLength={200} value={order} onChange={event => setOrder(event.target.value)} /></label>
        <label>Part number<input maxLength={200} value={part} onChange={event => setPart(event.target.value)} /></label>
        <label>Operation<input maxLength={200} value={operation} onChange={event => setOperation(event.target.value)} /></label>
        <label>Revision reason {head ? "(required)" : "(optional)"}<textarea required={Boolean(head)} maxLength={2000} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <button type="submit">Save target revision</button>
      </fieldset>
    </form>
    {pendingId && <p role="alert">Save outcome is uncertain. Reload history to reconcile, or retry with the same revision ID; do not create another revision.</p>}
    {status && <p role="status">{status}</p>}
    <p>Actual achieved: not established. No target approval, sealed baseline, target achievement, or OEE Performance qualification is implied.</p>
  </section>;
}
