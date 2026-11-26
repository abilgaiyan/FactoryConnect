import { useEffect, useState, type FormEvent } from "react";

type Scope = { machineId: string; shiftScheduleAssignmentId: string; shiftId: string; startsAtUtc: string; endsAtUtc: string };
type Revision = { cycleRevisionId: string; revisionNumber: number; idealCycleSeconds: number; enteredBy: string; recordedAtUtc: string; revisionReason: string | null; productionOrder: string | null; partNumber: string | null; operation: string | null };

export function SupervisorIdealCycle({ scope }: { readonly scope: Scope }) {
  const [history, setHistory] = useState<Revision[]>([]);
  const [seconds, setSeconds] = useState("");
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
  const head = history.reduce<Revision | null>((latest, item) => !latest || item.revisionNumber > latest.revisionNumber ? item : latest, null);
  const value = Number(seconds);
  const valid = /^(?:0|[1-9]\d*)(?:\.\d{1,3})?$/.test(seconds) && value > 0 && value <= 86400;
  async function save(event: FormEvent) {
    event.preventDefault();
    if (loading || saving || error) return;
    if (!pending && (!valid || (head && !reason.trim()))) { setStatus("Enter positive seconds (up to three decimal places) and a correction reason when revising."); return; }
    const id = pending?.id ?? crypto.randomUUID();
    const body = pending?.body ?? JSON.stringify({ cycleRevisionId: id, shiftScheduleAssignmentId: scope.shiftScheduleAssignmentId, shiftId: scope.shiftId, shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc, revisionNumber: (head?.revisionNumber ?? 0) + 1, expectedHeadRevisionId: head?.cycleRevisionId ?? null, idealCycleSeconds: value, productionOrder: head?.productionOrder ?? null, partNumber: head?.partNumber ?? null, operation: head?.operation ?? null, revisionReason: reason.trim() || null });
    setPending({id, body}); setSaving(true); setStatus("");
    try {
      const response = await fetch(url, {method:"POST", headers:{"Content-Type":"application/json"}, body});
      if (!response.ok) {
        if ([400,401,403,409,422].includes(response.status)) { setPending(null); setRefresh(n => n + 1); }
        throw new Error(`Save not confirmed (HTTP ${response.status}). Reload history before another attempt.`);
      }
      setPending(null); setSeconds(""); setReason(""); setStatus("Ideal cycle revision saved."); setRefresh(n => n + 1);
    } catch(e) { setStatus(String(e)); } finally { setSaving(false); }
  }
  return <section className="supervisor-editor" aria-label="Shift ideal cycle time">
    <h2>Ideal cycle time</h2>
    <p>Seconds per piece for this shift. Declaration only; not qualified OEE evidence.</p>
    <button type="button" disabled={loading || saving} onClick={() => setRefresh(n => n + 1)}>Reload ideal cycle history</button>
    {loading ? <p>Loading ideal cycle…</p> : error ? <p role="alert">{error}</p> : head ? <p><strong>{head.idealCycleSeconds} seconds/piece</strong> · {Number((3600/head.idealCycleSeconds).toFixed(2))} pieces/hour · Revision {head.revisionNumber}</p> : <p>No ideal cycle time recorded.</p>}
    <form onSubmit={save}>
      <fieldset disabled={loading || saving || Boolean(error)}>
        <legend>{head ? "Correct ideal cycle time" : "Set ideal cycle time"}</legend>
        <label>Ideal cycle time (seconds per piece)<input required={!pending} disabled={Boolean(pending)} inputMode="decimal" value={seconds} onChange={e => setSeconds(e.target.value)} /></label>
        {valid && <p>{Number((value/60).toFixed(3))} minutes/piece · {Number((3600/value).toFixed(2))} theoretical pieces/hour</p>}
        <label>Correction reason<textarea required={Boolean(head) && !pending} disabled={Boolean(pending)} maxLength={2000} value={reason} onChange={e => setReason(e.target.value)} /></label>
        <button type="submit">{pending ? "Retry same ideal cycle revision" : "Save ideal cycle time"}</button>
      </fieldset>
    </form>
    {pending && <p role="alert">Save outcome uncertain. Reload history or retry the same revision.</p>}
    {status && <p role="status">{status}</p>}
    {!loading && !error && history.length > 0 && <details><summary>Ideal cycle revision history</summary>{history.map(item => <p key={item.cycleRevisionId}>Revision {item.revisionNumber}: {item.idealCycleSeconds} seconds/piece · {item.enteredBy} · {item.recordedAtUtc} · {item.revisionReason || "Initial declaration"}</p>)}</details>}
  </section>;
}
