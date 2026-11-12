import { useEffect, useState, type FormEvent } from "react";

type Entry = {
  entryId: string; machineId: string; shiftScheduleAssignmentId: string; shiftId: string;
  shiftStartsAtUtc: string; shiftEndsAtUtc: string; productionOrder: string;
  partNumber: string; operation: string; note: string | null;
  enteredBy: string; recordedAtUtc: string; goodQuantity: number | null; rejectedQuantity: number | null;
};
type Scope = { machineId: string; shiftScheduleAssignmentId: string; shiftId: string; startsAtUtc: string; endsAtUtc: string };

export function SupervisorLocalEntryForm({ scope }: { readonly scope: Scope }) {
  const [entries, setEntries] = useState<Entry[]>([]);
  const [order, setOrder] = useState("");
  const [part, setPart] = useState("");
  const [operation, setOperation] = useState("");
  const [note, setNote] = useState("");
  const [good, setGood] = useState("");
  const [rejected, setRejected] = useState("");
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [status, setStatus] = useState("");
  const [loading, setLoading] = useState(false);
  const url = `/api/supervisor/v1/machines/${encodeURIComponent(scope.machineId)}/entries`;
  const query = new URLSearchParams({ shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc });

  async function refresh(signal?: AbortSignal) {
    const response = await fetch(`${url}?${query}`, signal ? { signal } : {});
    if (!response.ok) throw new Error(`Entry read unavailable (HTTP ${response.status})`);
    const body: unknown = await response.json();
    if (!Array.isArray(body)) throw new Error("Unexpected supervisor entry response");
    setEntries(body as Entry[]);
  }
  useEffect(() => {
    const controller = new AbortController();
    setEntries([]); setStatus("");
    void refresh(controller.signal).catch(error => {
      if (!controller.signal.aborted) setStatus(String(error));
    });
    return () => controller.abort();
  }, [url, scope.startsAtUtc, scope.endsAtUtc]);

  async function save(event: FormEvent) {
    event.preventDefault();
    if (loading) return;
    if (!/^\\d+$/.test(good) || !/^\\d+$/.test(rejected) ||
        !Number.isSafeInteger(Number(good)) || !Number.isSafeInteger(Number(rejected))) {
      setStatus("Enter nonnegative whole-number good and rejected quantities."); return;
    }
    setLoading(true); setStatus("");
    // Retain the same ID after an ambiguous network failure; never silently create a second declaration.
    const id = pendingId ?? crypto.randomUUID();
    setPendingId(id);
    try {
      const response = await fetch(url, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          entryId: id, shiftScheduleAssignmentId: scope.shiftScheduleAssignmentId,
          shiftId: scope.shiftId, shiftStartsAtUtc: scope.startsAtUtc,
          shiftEndsAtUtc: scope.endsAtUtc, productionOrder: order.trim(),
          partNumber: part.trim(), operation: operation.trim(), note: note.trim() || null,
          goodQuantity: Number(good), rejectedQuantity: Number(rejected)
        })
      });
      if (!response.ok) throw new Error(`Save not confirmed (HTTP ${response.status}). Do not submit a new entry until reconciled.`);
      // A successful POST is durable even if the subsequent list refresh fails.
      // Never present a read failure as a failed write or offer a duplicate retry.
      setPendingId(null); setOrder(""); setPart(""); setOperation(""); setNote(""); setGood(""); setRejected("");
      try {
        await refresh();
        setStatus("Entry saved to SQL. It is not published as CNC evidence or OEE.");
      } catch {
        setStatus("Entry saved to SQL, but the saved-entry list could not be refreshed. Use Refresh entries to view it.");
      }
    } catch (error) {
      setStatus(String(error));
    } finally {
      setLoading(false);
    }
  }
  return <section aria-label="Local supervisor production entry">
    <h2>Supervisor production entry · local commissioning</h2>
    <p>Saving requires an explicitly enabled loopback-only API. This entry records supervisor-declared quantities, not accepted CNC quantity or OEE. A failed save may be ambiguous; verify entries before trying again.</p>
    <form onSubmit={event => { void save(event); }}>
      <label>Production order<input required maxLength={200} value={order} onChange={event => { setOrder(event.target.value); setPendingId(null); }} /></label>
      <label>Part number<input required maxLength={200} value={part} onChange={event => { setPart(event.target.value); setPendingId(null); }} /></label>
      <label>Operation<input required maxLength={200} value={operation} onChange={event => { setOperation(event.target.value); setPendingId(null); }} /></label>
      <label>Good quantity<input required type="number" min="0" step="1" value={good} onChange={event => { setGood(event.target.value); setPendingId(null); }} /></label>
      <label>Rejected quantity<input required type="number" min="0" step="1" value={rejected} onChange={event => { setRejected(event.target.value); setPendingId(null); }} /></label>
      <label>Note<textarea maxLength={2000} value={note} onChange={event => { setNote(event.target.value); setPendingId(null); }} /></label>
      <button type="submit" disabled={loading}>{loading ? "Saving…" : pendingId ? "Retry same entry" : "Save production entry"}</button>
      <button type="button" disabled={loading} onClick={() => { void refresh().then(() => setStatus("Entries refreshed."), error => setStatus(String(error))); }}>Refresh entries</button>
    </form>
    {status && <p role="status">{status}</p>}
    <h3>Saved entries ({entries.length})</h3>
    {entries.length === 0 ? <p>No saved entries returned for this exact shift. This does not prove zero production.</p> :
      <ul>{entries.map(entry => <li key={entry.entryId}>{entry.productionOrder} · {entry.partNumber} · {entry.operation} · Good {entry.goodQuantity ?? "Not declared"} · Rejected {entry.rejectedQuantity ?? "Not declared"} · {entry.enteredBy} · {entry.recordedAtUtc}</li>)}</ul>}
  </section>;
}
