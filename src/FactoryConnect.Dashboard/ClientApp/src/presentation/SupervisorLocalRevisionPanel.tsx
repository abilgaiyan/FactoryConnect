import { useEffect, useState } from "react";

type Entry = { entryId: string; productionOrder: string; partNumber: string; operation: string; goodQuantity: number | null; rejectedQuantity: number | null };
type Revision = { revisionId: string; revisionNumber: number; productionOrder: string; partNumber: string; operation: string; note: string | null; goodQuantity: number | null; rejectedQuantity: number | null; correctionReason: string; revisedBy: string; revisedAtUtc: string };

export function SupervisorLocalRevisionPanel({ machineId, entry }: { readonly machineId: string; readonly entry: Entry }) {
  const [history, setHistory] = useState<Revision[]>([]);
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [order, setOrder] = useState(entry.productionOrder);
  const [part, setPart] = useState(entry.partNumber);
  const [operation, setOperation] = useState(entry.operation);
  const [note, setNote] = useState("");
  const [good, setGood] = useState(entry.goodQuantity?.toString() ?? "");
  const [rejected, setRejected] = useState(entry.rejectedQuantity?.toString() ?? "");
  const [reason, setReason] = useState("");
  const [retry, setRetry] = useState<string | null>(null);
  const url = `/api/supervisor/v1/machines/${encodeURIComponent(machineId)}/entries/${encodeURIComponent(entry.entryId)}/revisions`;

  async function refresh(signal?: AbortSignal) {
    const response = await fetch(url, signal ? { signal } : {});
    if (!response.ok) throw new Error(`Revision history unavailable (HTTP ${response.status})`);
    const value: unknown = await response.json();
    if (!Array.isArray(value)) throw new Error("Invalid revision history");
    setHistory(value as Revision[]);
  }

  useEffect(() => {
    const controller = new AbortController();
    setHistory([]); setEditing(false); setStatus(""); setRetry(null);
    void refresh(controller.signal).catch(error => { if (!controller.signal.aborted) setStatus(String(error)); });
    return () => controller.abort();
  }, [url]);

  const head = history.at(-1);
  function begin() {
    setOrder(head?.productionOrder ?? entry.productionOrder);
    setPart(head?.partNumber ?? entry.partNumber);
    setOperation(head?.operation ?? entry.operation);
    setNote(head?.note ?? "");
    setGood((head?.goodQuantity ?? entry.goodQuantity)?.toString() ?? "");
    setRejected((head?.rejectedQuantity ?? entry.rejectedQuantity)?.toString() ?? "");
    setReason(""); setRetry(null); setEditing(true);
  }

  async function submit() {
    if (busy) return;
    if (!order.trim() || !part.trim() || !operation.trim() || !reason.trim()) {
      setStatus("Production details and correction reason are required."); return;
    }
    if ((good === "") !== (rejected === "") || (good !== "" && (!/^\d+$/.test(good) || !/^\d+$/.test(rejected) || !Number.isSafeInteger(Number(good)) || !Number.isSafeInteger(Number(rejected))))) {
      setStatus("Enter both nonnegative quantities or leave both blank."); return;
    }
    const revisionId = retry ?? crypto.randomUUID();
    setRetry(revisionId); setBusy(true); setStatus("");
    try {
      const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ revisionId, expectedRevisionId: head?.revisionId ?? null,
          revisionNumber: (head?.revisionNumber ?? 0) + 1, productionOrder: order.trim(),
          partNumber: part.trim(), operation: operation.trim(), note: note || null,
          goodQuantity: good === "" ? null : Number(good), rejectedQuantity: rejected === "" ? null : Number(rejected),
          correctionReason: reason.trim() }) });
      if (!response.ok) throw new Error(`Correction not confirmed (HTTP ${response.status}). Refresh history before changing values.`);
      setRetry(null); setEditing(false);
      await refresh(); setStatus("Correction recorded as a new pending revision. No OEE evidence published.");
    } catch (error) { setStatus(String(error)); }
    finally { setBusy(false); }
  }

  return <div aria-label="Declaration correction history">
    <p>Corrections: {history.length}. Original declarations and review decisions are preserved.</p>
    <button type="button" disabled={busy} onClick={() => { void refresh().then(() => { setRetry(null); setStatus("Revision history refreshed."); }, error => setStatus(String(error))); }}>Refresh corrections</button>
    {!editing && <button type="button" disabled={busy} onClick={begin}>Correct declaration</button>}
    {status && <p role="status">{status}</p>}
    {editing && <div>
      <label>Production order<input maxLength={200} value={order} onChange={event => setOrder(event.target.value)} /></label>
      <label>Part number<input maxLength={200} value={part} onChange={event => setPart(event.target.value)} /></label>
      <label>Operation<input maxLength={200} value={operation} onChange={event => setOperation(event.target.value)} /></label>
      <label>Note<textarea maxLength={2000} value={note} onChange={event => setNote(event.target.value)} /></label>
      <label>Good quantity<input inputMode="numeric" value={good} onChange={event => setGood(event.target.value)} /></label>
      <label>Rejected quantity<input inputMode="numeric" value={rejected} onChange={event => setRejected(event.target.value)} /></label>
      <label>Correction reason<textarea required maxLength={2000} value={reason} onChange={event => setReason(event.target.value)} /></label>
      <button type="button" disabled={busy || !reason.trim() || retry !== null && busy} onClick={() => { void submit(); }}>Save pending correction</button>
      <button type="button" disabled={busy || retry !== null} onClick={() => setEditing(false)}>Cancel</button>
    </div>}
    <ol>{history.map(item => <li key={item.revisionId}>
      Revision {item.revisionNumber} · Pending review · {item.productionOrder} · {item.partNumber} · {item.operation}
      · Good {item.goodQuantity ?? "Missing"} · Rejected {item.rejectedQuantity ?? "Missing"}
      · {item.revisedBy} · {item.revisedAtUtc} · Reason: {item.correctionReason}
    </li>)}</ol>
  </div>;
}
