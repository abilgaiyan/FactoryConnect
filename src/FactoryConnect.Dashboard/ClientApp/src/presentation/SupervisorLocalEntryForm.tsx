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
  const [reviewFilter, setReviewFilter] = useState("");
  const [reviewOrder, setReviewOrder] = useState("");
  const filteredEntries = entries.filter(entry =>
    (!reviewOrder || entry.productionOrder === reviewOrder) &&
    (!reviewFilter || [entry.partNumber, entry.operation, entry.productionOrder, entry.enteredBy]
      .some(value => value.toLocaleLowerCase().includes(reviewFilter.toLocaleLowerCase()))));
  const orders = [...new Set(entries.map(entry => entry.productionOrder))].sort();
  const quantifiedEntries = filteredEntries.filter(entry =>
    Number.isSafeInteger(entry.goodQuantity) && Number.isSafeInteger(entry.rejectedQuantity) &&
    entry.goodQuantity !== null && entry.rejectedQuantity !== null &&
    entry.goodQuantity >= 0 && entry.rejectedQuantity >= 0);
  const declaredGood = quantifiedEntries.reduce((total, entry) => total + BigInt(entry.goodQuantity!), 0n);
  const declaredRejected = quantifiedEntries.reduce((total, entry) => total + BigInt(entry.rejectedQuantity!), 0n);
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

  function exportFilteredEntries() {
    // Prefix spreadsheet formulas to prevent execution when a CSV is opened in Excel.
    const cell = (value: string | number | null) => {
      const text = String(value ?? "");
      const safe = /^[\\s]*[=+@-]/.test(text) ? "'" + text : text;
      return '"' + safe.replace(/"/g, '""') + '"';
    };
    const columns = [
      "Entry ID", "Machine ID", "Shift assignment", "Shift", "Shift start UTC",
      "Shift end UTC", "Production order", "Part number", "Operation",
      "Good quantity (declared)", "Rejected quantity (declared)", "Supervisor",
      "Recorded UTC", "Note"
    ];
    const lines = [
      columns.map(cell).join(","),
      ...filteredEntries.map(entry => [
        entry.entryId, entry.machineId, entry.shiftScheduleAssignmentId, entry.shiftId,
        entry.shiftStartsAtUtc, entry.shiftEndsAtUtc, entry.productionOrder,
        entry.partNumber, entry.operation, entry.goodQuantity, entry.rejectedQuantity,
        entry.enteredBy, entry.recordedAtUtc, entry.note
      ].map(cell).join(","))
    ];
    const blob = new Blob(["\\uFEFF", lines.join("\\r\\n")], { type: "text/csv;charset=utf-8" });
    const href = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = "supervisor-declarations.csv";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(href);
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (loading) return;
    if (!/^\d+$/.test(good) || !/^\d+$/.test(rejected) ||
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
    <h3>Saved entries ({filteredEntries.length} of {entries.length})</h3>
    <p>Review entries for this exact machine and shift. Filters affect only the display, not saved records or OEE.</p>
    <label>Filter production order<select value={reviewOrder} onChange={event => setReviewOrder(event.target.value)}>
      <option value="">All production orders</option>
      {orders.map(value => <option key={value} value={value}>{value}</option>)}
    </select></label>
    <label>Search part, operation, order or supervisor<input type="search" value={reviewFilter} onChange={event => setReviewFilter(event.target.value)} /></label>
    <button type="button" disabled={filteredEntries.length === 0} onClick={exportFilteredEntries}>Export filtered entries (CSV)</button>
    <p aria-label="Filtered supervisor declaration totals">Declared good: {declaredGood.toString()} · Declared rejected: {declaredRejected.toString()} · Quantified entries: {quantifiedEntries.length} of {filteredEntries.length}. These are sums of declarations, not reconciled unique production or OEE.</p>
    {entries.length === 0 ? <p>No saved entries returned for this exact shift. This does not prove zero production.</p> :
      filteredEntries.length === 0 ? <p>No saved entries match the review filters. Clear the filters to see all entries for this shift.</p> :
      <ul>{filteredEntries.map(entry => <li key={entry.entryId}>{entry.productionOrder} · {entry.partNumber} · {entry.operation} · Good {entry.goodQuantity ?? "Not declared"} · Rejected {entry.rejectedQuantity ?? "Not declared"} · {entry.enteredBy} · {entry.recordedAtUtc}</li>)}</ul>}
  </section>;
}
