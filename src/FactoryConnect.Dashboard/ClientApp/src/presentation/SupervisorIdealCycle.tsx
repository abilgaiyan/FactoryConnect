import { formatReportTimestamp } from "./date-formatting.ts";
import { useEffect, useState, type FormEvent } from "react";

type Scope = { machineId: string; shiftScheduleAssignmentId: string; shiftId: string; startsAtUtc: string; endsAtUtc: string };
type Part = { partNumber: string; displayName: string; idealCycleSeconds: number };
type Operation = { partNumber: string; operationCode: string; displayName: string; idealCycleSeconds: number | null };
const pageSize = 25;
type Revision = { cycleRevisionId: string; revisionNumber: number; idealCycleSeconds: number; enteredBy: string; recordedAtUtc: string; revisionReason: string | null; productionOrder: string | null; partNumber: string | null; operation: string | null };

export function SupervisorIdealCycle({ scope }: { readonly scope: Scope }) {
  const [contextKey, setContextKey] = useState("");
  const [history, setHistory] = useState<Revision[]>([]);
  const [seconds, setSeconds] = useState("");
  const [parts, setParts] = useState<Part[]>([]);
  const [operations, setOperations] = useState<Operation[]>([]);
  const [part, setPart] = useState<Part | null>(null);
  const [operation, setOperation] = useState<Operation | null>(null);
  const [partOffset, setPartOffset] = useState(0);
  const [operationOffset, setOperationOffset] = useState(0);
  const [masterError, setMasterError] = useState("");
  const [operationError, setOperationError] = useState("");
  const [partsLoading, setPartsLoading] = useState(true);
  const [operationsLoading, setOperationsLoading] = useState(false);
  const [order, setOrder] = useState("");
  const selectedPart = part?.partNumber ?? "";
  useEffect(() => {
    const controller = new AbortController();
    setPartsLoading(true); setMasterError(""); setParts([]);
    void fetch("/api/supervisor/v1/parts?offset=" + partOffset + "&limit=" + pageSize, { signal: controller.signal, cache: "no-store", redirect: "error" })
      .then(async response => {
        if (!response.ok) throw new Error("Parts unavailable (HTTP " + response.status + ")");
        const data: unknown = await response.json();
        if (!Array.isArray(data) || !data.every(p => p && typeof p.partNumber === "string" && typeof p.displayName === "string" && typeof p.idealCycleSeconds === "number" && p.idealCycleSeconds > 0 && p.idealCycleSeconds <= 86400 && p.isActive === true)) throw new Error("Invalid part response");
        if (!controller.signal.aborted) setParts(data);
      }).catch(e => { if (!controller.signal.aborted) setMasterError(String(e)); })
      .finally(() => { if (!controller.signal.aborted) setPartsLoading(false); });
    return () => controller.abort();
  }, [partOffset]);
  useEffect(() => {
    if (!selectedPart) return;
    const controller = new AbortController();
    setOperationsLoading(true); setOperationError(""); setOperations([]);
    void fetch("/api/supervisor/v1/operations?partNumber=" + encodeURIComponent(selectedPart) + "&offset=" + operationOffset + "&limit=" + pageSize, { signal: controller.signal, cache: "no-store", redirect: "error" })
      .then(async response => {
        if (!response.ok) throw new Error("Operations unavailable (HTTP " + response.status + ")");
        const data: unknown = await response.json();
        if (!Array.isArray(data) || !data.every(o => o && o.partNumber === selectedPart && typeof o.operationCode === "string" && typeof o.displayName === "string" && (o.idealCycleSeconds === null || (typeof o.idealCycleSeconds === "number" && o.idealCycleSeconds > 0 && o.idealCycleSeconds <= 86400)) && o.isActive === true)) throw new Error("Invalid operation response");
        if (!controller.signal.aborted) setOperations(data);
      }).catch(e => { if (!controller.signal.aborted) setOperationError(String(e)); })
      .finally(() => { if (!controller.signal.aborted) setOperationsLoading(false); });
    return () => controller.abort();
  }, [selectedPart, operationOffset]);
  const [reason, setReason] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [pending, setPending] = useState<{id: string; body: string} | null>(null);
  const url = `/api/supervisor/v1/machines/${encodeURIComponent(scope.machineId)}/ideal-cycles`;
  const query = new URLSearchParams({ assignmentId: scope.shiftScheduleAssignmentId, shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc }).toString();
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError("");
    void fetch(`${url}?${query}`, {signal: controller.signal}).then(async response => {
      if (!response.ok) throw new Error(`Ideal cycle history unavailable (HTTP ${response.status})`);
      const body: unknown = await response.json();
      if (!Array.isArray(body)) throw new Error("Unexpected ideal cycle history");
      if (controller.signal.aborted) return;
      setHistory(body as Revision[]);
      if (pending && body.some((item: Revision) => item.cycleRevisionId === pending.id)) {
        setPending(null); setSeconds(""); setReason(""); setStatus("Previous save confirmed.");
      }
    }).catch(e => { if (!controller.signal.aborted) setError(String(e)); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [url, query, refresh]);
  const keyOf = (item: Revision) => JSON.stringify([item.productionOrder, item.partNumber, item.operation]);
  const heads = new Map<string, Revision>();
  for (const item of history) {
    const key = keyOf(item);
    const previous = heads.get(key);
    if (!previous || item.revisionNumber > previous.revisionNumber) heads.set(key, item);
  }
  const newKey = JSON.stringify([order.trim() || null, part?.partNumber ?? null, operation?.operationCode ?? null]);
  const head = contextKey ? heads.get(contextKey) ?? null : heads.get(newKey) ?? null;
  const missingContext = Boolean(contextKey && !head);
  const value = Number(seconds);
  const valid = /^(?:0|[1-9]\d*)(?:\.\d{1,3})?$/.test(seconds) && value > 0 && value <= 86400;
  async function save(event: FormEvent) {
    event.preventDefault();
    if (loading || saving || error || missingContext) return;
    if (!pending && (!valid || (head && !reason.trim()))) { setStatus("Enter positive seconds (up to three decimal places) and a correction reason when revising."); return; }
    const id = pending?.id ?? crypto.randomUUID();
    const body = pending?.body ?? JSON.stringify({ cycleRevisionId: id, shiftScheduleAssignmentId: scope.shiftScheduleAssignmentId, shiftId: scope.shiftId, shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc, revisionNumber: (head?.revisionNumber ?? 0) + 1, expectedHeadRevisionId: head?.cycleRevisionId ?? null, idealCycleSeconds: value, productionOrder: head ? head.productionOrder : order.trim() || null, partNumber: head ? head.partNumber : part?.partNumber ?? null, operation: head ? head.operation : operation?.operationCode ?? null, revisionReason: reason.trim() || null });
    setPending({id, body}); setSaving(true); setStatus("");
    try {
      const response = await fetch(url, {method:"POST", headers:{"Content-Type":"application/json"}, body});
      if (!response.ok) {
        if ([400,401,403,409,422].includes(response.status)) { setPending(null); setRefresh(n => n + 1); }
        throw new Error(`Save not confirmed (HTTP ${response.status}). Reload history before another attempt.`);
      }
      setPending(null); setContextKey(contextKey || newKey); setSeconds(""); setReason(""); setStatus("Ideal cycle revision saved."); setRefresh(n => n + 1);
    } catch(e) { setStatus(String(e)); } finally { setSaving(false); }
  }
  return <section className="supervisor-editor" aria-label="Shift ideal cycle time">
    <h2>Ideal cycle time</h2>
    <p>Seconds per piece for this shift. Declaration only; not qualified OEE evidence.</p>
    <button type="button" disabled={loading || saving} onClick={() => setRefresh(n => n + 1)}>Reload ideal cycle history</button>
    {loading ? <p>Loading ideal cycle…</p> : error ? <p role="alert">{error}</p> : head ? <p><strong>{head.idealCycleSeconds} seconds/piece</strong> · {Number((3600/head.idealCycleSeconds).toFixed(2))} pieces/hour · Revision {head.revisionNumber}</p> : <p>No ideal cycle time recorded.</p>}
    <label>Production context<select value={contextKey} disabled={loading || saving || Boolean(error) || Boolean(pending)} onChange={e => {
      setContextKey(e.target.value); setSeconds(""); setReason(""); setStatus("");
      setOrder(""); setPart(null); setOperation(null); setOperations([]); setOperationOffset(0);
    }}><option value="">New production context</option>
      {[...heads.entries()].map(([key, item]) => <option key={key} value={key}>{item.productionOrder ?? "No order"} · {item.partNumber ?? "No part"} · {item.operation ?? "No operation"}</option>)}
    </select></label>
    {missingContext && <p role="alert">Selected context is unavailable. Select another context.</p>}
    <form onSubmit={save}>
      <fieldset disabled={loading || saving || Boolean(error) || missingContext}>
        <legend>{head ? "Correct ideal cycle time" : "Set ideal cycle time"}</legend>
        {!contextKey && <fieldset disabled={Boolean(pending)}>
          <legend>Production context (optional)</legend>
          <label>Production order<input maxLength={200} placeholder="e.g. WO-2026-001" value={order} onChange={e => setOrder(e.target.value)} /></label>
          {masterError && <p role="alert">{masterError}</p>}
          <label>Part<select disabled={partsLoading} value={selectedPart} onChange={e => {
            const next = parts.find(p => p.partNumber === e.target.value) ?? null;
            setPart(next); setOperation(null); setOperations([]); setOperationOffset(0); setOperationError(""); setSeconds(next ? String(next.idealCycleSeconds) : "");
          }}><option value="">Manual cycle entry</option>
            {part && !parts.some(p => p.partNumber === selectedPart) && <option value={selectedPart}>{selectedPart} — {part.displayName}</option>}
            {parts.map(p => <option key={p.partNumber} value={p.partNumber}>{p.partNumber} — {p.displayName}</option>)}
          </select></label>
          <button type="button" disabled={partsLoading || partOffset === 0} onClick={() => setPartOffset(v => Math.max(0, v - pageSize))}>Previous parts</button>
          <button type="button" disabled={partsLoading || parts.length < pageSize} onClick={() => setPartOffset(v => v + pageSize)}>Next parts</button>
          {part && <>
            {operationError && <p role="alert">{operationError}</p>}
            <label>Operation<select disabled={operationsLoading} value={operation?.operationCode ?? ""} onChange={e => {
              const next = operations.find(o => o.operationCode === e.target.value) ?? null;
              setOperation(next); setSeconds(String(next?.idealCycleSeconds ?? part.idealCycleSeconds));
            }}><option value="">Use part default</option>
              {operation && !operations.some(o => o.operationCode === operation.operationCode) && <option value={operation.operationCode}>{operation.displayName}</option>}
              {operations.map(o => <option key={o.operationCode} value={o.operationCode}>{o.operationCode} — {o.displayName}</option>)}
            </select></label>
            <button type="button" disabled={operationsLoading || operationOffset === 0} onClick={() => setOperationOffset(v => Math.max(0, v - pageSize))}>Previous operations</button>
            <button type="button" disabled={operationsLoading || operations.length < pageSize} onClick={() => setOperationOffset(v => v + pageSize)}>Next operations</button>
            <p>Suggested cycle: {operation?.idealCycleSeconds ?? part.idealCycleSeconds} seconds ({operation?.idealCycleSeconds != null ? "operation override" : "part default"}). You may adjust before saving.</p>
          </>}
        </fieldset>}
        {head && <p>Recorded context: {head.productionOrder ?? "No order"} · {head.partNumber ?? "No part"} · {head.operation ?? "No operation"}</p>}
        <label>Ideal cycle time (seconds per piece)<input placeholder="e.g. 450" required={!pending} disabled={Boolean(pending)} inputMode="decimal" value={seconds} onChange={e => setSeconds(e.target.value)} /></label>
        {valid && <p>{Number((value/60).toFixed(3))} minutes/piece · {Number((3600/value).toFixed(2))} theoretical pieces/hour</p>}
        <label>Correction reason<textarea placeholder="e.g. Revised after tool change" required={Boolean(head) && !pending} disabled={Boolean(pending)} maxLength={2000} value={reason} onChange={e => setReason(e.target.value)} /></label>
        <button type="submit">{pending ? "Retry same ideal cycle revision" : "Save ideal cycle time"}</button>
      </fieldset>
    </form>
    {pending && <p role="alert">Save outcome uncertain. Reload history or retry the same revision.</p>}
    {status && <p role="status">{status}</p>}
    {!loading && !error && history.length > 0 && <details><summary>Ideal cycle revision history</summary>{history.map(item => <p key={item.cycleRevisionId}>Revision {item.revisionNumber}: {item.idealCycleSeconds} seconds/piece · {item.enteredBy} · {formatReportTimestamp(item.recordedAtUtc)} · {item.productionOrder ?? "No order"} · {item.partNumber ?? "No part"} / {item.operation ?? "No operation"} · {item.revisionReason || "Initial declaration"}</p>)}</details>}
  </section>;
}
