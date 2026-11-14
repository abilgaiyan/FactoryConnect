import { useEffect, useState } from "react";
import { SupervisorLocalRevisionPanel } from "./SupervisorLocalRevisionPanel";

type Scope = { machineId: string; startsAtUtc: string; endsAtUtc: string };
type Declaration = { entryId: string; productionOrder: string; partNumber: string; operation: string; enteredBy: string; goodQuantity: number | null; rejectedQuantity: number | null };
type Review = { entryId: string; decisionId: string | null; decision: "Pending" | "Approved" | "Rejected"; reason: string | null; reviewedBy: string | null; reviewedAtUtc: string | null };
type Pending = { entryId: string; decisionId: string; decision: "Approved" | "Rejected"; reason: string };

export function SupervisorLocalReviewPanel({ scope, entries }: { readonly scope: Scope; readonly entries: readonly Declaration[] }) {
  const [reviews, setReviews] = useState<Review[]>([]);
  const [status, setStatus] = useState("");
  const [reason, setReason] = useState("");
  const [selected, setSelected] = useState("");
  const [pending, setPending] = useState<Pending | null>(null);
  const [busy, setBusy] = useState(false);
  const base = `/api/supervisor/v1/machines/${encodeURIComponent(scope.machineId)}`;
  const query = new URLSearchParams({ shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc });

  async function refresh(signal?: AbortSignal) {
    const response = await fetch(`${base}/reviews?${query}`, signal ? { signal } : {});
    if (!response.ok) throw new Error(`Review read unavailable (HTTP ${response.status})`);
    const data: unknown = await response.json();
    if (!Array.isArray(data)) throw new Error("Invalid review response");
    setReviews(data as Review[]);
  }

  useEffect(() => {
    const controller = new AbortController();
    setReviews([]); setStatus(""); setSelected(""); setReason(""); setPending(null);
    void refresh(controller.signal).catch(error => {
      if (!controller.signal.aborted) setStatus(String(error));
    });
    return () => controller.abort();
  }, [base, scope.startsAtUtc, scope.endsAtUtc]);

  const byEntry = new Map(reviews.map(review => [review.entryId, review]));
  const pendingCount = entries.filter(entry => (byEntry.get(entry.entryId)?.decision ?? "Pending") === "Pending").length;
  const approvedCount = entries.filter(entry => byEntry.get(entry.entryId)?.decision === "Approved").length;
  const rejectedCount = entries.filter(entry => byEntry.get(entry.entryId)?.decision === "Rejected").length;

  async function decide(entryId: string, decision: "Approved" | "Rejected") {
    if (busy) return;
    const trimmed = reason.trim();
    if (!trimmed) { setStatus("A review reason is required."); return; }
    if (pending && (pending.entryId !== entryId || pending.decision !== decision || pending.reason !== trimmed)) {
      setStatus("A previous decision may have been saved. Refresh reviews to reconcile before changing it.");
      return;
    }
    const attempt = pending ?? { entryId, decision, reason: trimmed, decisionId: crypto.randomUUID() };
    setPending(attempt); setBusy(true); setStatus("");
    try {
      const response = await fetch(`${base}/entries/${encodeURIComponent(entryId)}/review`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ decisionId: attempt.decisionId, decision: attempt.decision, reason: attempt.reason })
      });
      if (!response.ok) throw new Error(`Decision not confirmed (HTTP ${response.status}). Refresh before changing the decision.`);
      setPending(null); setReason(""); setSelected("");
      try { await refresh(); setStatus("Local review decision saved. No production quantity or OEE was published."); }
      catch { setStatus("Decision saved, but review refresh failed. Use Refresh reviews."); }
    } catch (error) { setStatus(String(error)); }
    finally { setBusy(false); }
  }

  return <section aria-label="Local declaration review">
    <h3>Declaration review · local commissioning only</h3>
    <p>Pending: {pendingCount} · Approved: {approvedCount} · Rejected: {rejectedCount}. Decisions are immutable local review records, not authenticated factory approvals or qualified OEE evidence.</p>
    <button type="button" disabled={busy} onClick={() => {
      void refresh().then(() => { setPending(null); setStatus("Review decisions refreshed."); }, error => setStatus(String(error)));
    }}>Refresh reviews</button>
    {status && <p role="status">{status}</p>}
    <ul>{entries.map(entry => {
      const review = byEntry.get(entry.entryId);
      const decision = review?.decision ?? "Pending";
      return <li key={entry.entryId}>
        <p>{entry.productionOrder} · {entry.partNumber} · {entry.operation} · {entry.enteredBy} · Good {entry.goodQuantity ?? "Missing"} · Rejected {entry.rejectedQuantity ?? "Missing"} · <strong>{decision}</strong></p>
        {review?.decisionId ? <p>Reviewer: {review.reviewedBy} · UTC: {review.reviewedAtUtc} · Reason: {review.reason} · Decision ID: {review.decisionId}</p> :
          selected === entry.entryId ? <div>
            <label>Review reason<textarea required maxLength={2000} value={reason} onChange={event => setReason(event.target.value)} /></label>
            <button type="button" disabled={busy || !reason.trim() || (pending !== null && (pending.entryId !== entry.entryId || pending.decision !== "Approved"))} onClick={() => { void decide(entry.entryId, "Approved"); }}>Approve locally</button>
            <button type="button" disabled={busy || !reason.trim() || (pending !== null && (pending.entryId !== entry.entryId || pending.decision !== "Rejected"))} onClick={() => { void decide(entry.entryId, "Rejected"); }}>Reject locally</button>
            <button type="button" disabled={busy || pending !== null} onClick={() => { setSelected(""); setReason(""); }}>Cancel</button>
          </div> : <button type="button" disabled={busy || pending !== null} onClick={() => { setSelected(entry.entryId); setReason(""); }}>Review entry</button>}
        <SupervisorLocalRevisionPanel machineId={scope.machineId} entry={entry} />
      </li>;
    })}</ul>
  </section>;
}
