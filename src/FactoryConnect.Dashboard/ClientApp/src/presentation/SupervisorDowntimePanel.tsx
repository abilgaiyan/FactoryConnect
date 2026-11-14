import { useEffect, useState } from 'react';
import type { ActivityReport, ActivityInterval } from '../application/machine-activity-report.ts';

type Explanation = { explanationId: string; observationPosition: number; reasonCode: string; explanation: string; enteredBy: string };
type Review = { explanationId: string; decision: string; reviewedBy: string | null; reason: string | null };
type PendingAction = { kind: 'submit' | 'review'; id: string; payload: object };
const explainable = (interval: ActivityInterval) => ['stopped', 'fault', 'offline'].includes(interval.state)
  && BigInt(interval.position) <= 9007199254740991n
  && interval.sourceStartedAtUtc === interval.startedAtUtc
  && interval.sourceEndedAtUtc === interval.endedAtUtc;

export function SupervisorDowntimePanel({ report }: { readonly report: ActivityReport }) {
  const [entries, setEntries] = useState<Explanation[]>([]);
  const [reviews, setReviews] = useState<Review[]>([]);
  const [selected, setSelected] = useState('');
  const [reasonCode, setReasonCode] = useState('');
  const [explanation, setExplanation] = useState('');
  const [reviewReason, setReviewReason] = useState('');
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');
  const base = `/api/supervisor/v1/machines/${encodeURIComponent(report.machineId)}`;
  const query = new URLSearchParams({ fromUtc: report.fromInclusive, toUtc: report.toExclusive });
  const intervals = report.intervals.filter(explainable);
  const entryByPosition = new Map(entries.map(entry => [String(entry.observationPosition), entry]));
  const reviewByEntry = new Map(reviews.map(review => [review.explanationId, review]));
  const interval = intervals.find(item => item.position === selected);

  async function refresh(signal?: AbortSignal) {
    const options = signal ? { signal } : {};
    const [explanationsResponse, reviewsResponse] = await Promise.all([
      fetch(`${base}/downtime-explanations?${query}`, options),
      fetch(`${base}/downtime-reviews?${query}`, options)
    ]);
    if (!explanationsResponse.ok || !reviewsResponse.ok)
      throw new Error(`Local downtime read unavailable (HTTP ${explanationsResponse.status}/${reviewsResponse.status}).`);
    const [explanations, decisions]: unknown[] = await Promise.all([explanationsResponse.json(), reviewsResponse.json()]);
    if (!Array.isArray(explanations) || !Array.isArray(decisions)) throw new Error('Invalid downtime response.');
    setEntries(explanations as Explanation[]); setReviews(decisions as Review[]);
  }
  useEffect(() => {
    const controller = new AbortController();
    setEntries([]); setReviews([]); setSelected(''); setPending(null); setStatus('');
    void refresh(controller.signal).catch(error => { if (!controller.signal.aborted) setStatus(String(error)); });
    return () => controller.abort();
  }, [base, report.fromInclusive, report.toExclusive]);

  async function send(kind: 'submit' | 'review', id: string, payload: object, url: string) {
    if (busy) return;
    if (pending && (pending.kind !== kind || pending.id !== id ||
      (kind === 'review' && ((pending.payload as { decision: string; reason: string }).decision !== (payload as { decision: string; reason: string }).decision ||
        (pending.payload as { decision: string; reason: string }).reason !== (payload as { decision: string; reason: string }).reason)) ||
      (kind === 'submit' && ((pending.payload as { reasonCode: string; explanation: string }).reasonCode !== (payload as { reasonCode: string; explanation: string }).reasonCode ||
        (pending.payload as { reasonCode: string; explanation: string }).explanation !== (payload as { reasonCode: string; explanation: string }).explanation)))) {
      setStatus('Previous action may have been saved. Refresh before changing the request.'); return;
    }
    const action = pending ?? { kind, id, payload };
    setPending(action); setBusy(true); setStatus('');
    try {
      const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(action.payload) });
      if (!response.ok) throw new Error(`Action not confirmed (HTTP ${response.status}). Refresh before retrying with a new ID.`);
      setPending(null); setReasonCode(''); setExplanation(''); setReviewReason('');
      try { await refresh(); setStatus('Local downtime record saved. Machine activity and OEE are unchanged.'); }
      catch { setStatus('Saved, but refresh failed. Use Refresh records.'); }
    } catch (error) { setStatus(String(error)); }
    finally { setBusy(false); }
  }
  function submit() {
    if (!interval || !reasonCode.trim() || !explanation.trim()) return;
    const payload = { explanationId: crypto.randomUUID(), stateProcessorId: report.stateProcessorId,
      observationPosition: Number(interval.position), activityStartsAtUtc: interval.sourceStartedAtUtc,
      activityEndsAtUtc: interval.sourceEndedAtUtc, reasonCode: reasonCode.trim(), explanation: explanation.trim() };
    void send('submit', interval.position, payload, `${base}/downtime-explanations`);
  }
  function decide(entry: Explanation, decision: 'Approved' | 'Rejected') {
    if (!reviewReason.trim()) return;
    const payload = { decisionId: crypto.randomUUID(), decision, reason: reviewReason.trim() };
    void send('review', entry.explanationId, payload, `${base}/downtime-explanations/${encodeURIComponent(entry.explanationId)}/review`);
  }
  return <section aria-label="Supervisor downtime explanations">
    <h3>Downtime explanations · local commissioning only</h3>
    <p>Only complete, stored stopped/fault/offline intervals are eligible. Clipped intervals and unrepresented time cannot be explained here. These are local records, not approved OEE evidence.</p>
    <button type="button" disabled={busy} onClick={() => void refresh().then(() => { setPending(null); setStatus('Records refreshed.'); }, error => setStatus(String(error)))}>Refresh records</button>
    {status && <p role="status">{status}</p>}
    <label>Stored downtime interval <select value={selected} disabled={busy} onChange={event => { setSelected(event.target.value); setReasonCode(''); setExplanation(''); setReviewReason(''); }}>
      <option value="">Select an interval</option>
      {intervals.map(item => <option key={item.position} value={item.position}>{item.state} · {item.sourceStartedAtUtc} · position {item.position}</option>)}
    </select></label>
    {interval && (() => {
      const entry = entryByPosition.get(interval.position);
      const review = entry && reviewByEntry.get(entry.explanationId);
      return entry ? <div>
        <p><strong>{entry.reasonCode}</strong> — {entry.explanation} · Entered by {entry.enteredBy} · <strong>{review?.decision ?? 'Pending'}</strong></p>
        {review?.decision && review.decision !== 'Pending' ? <p>Reviewed by {review.reviewedBy}: {review.reason}</p> : <>
          <label>Review reason <textarea maxLength={2000} value={reviewReason} disabled={busy} onChange={event => setReviewReason(event.target.value)} /></label>
          <button type="button" disabled={busy || !reviewReason.trim() || (pending !== null && (pending.kind !== 'review' || pending.id !== entry.explanationId || (pending.payload as { decision: string; reason: string }).decision !== 'Approved' || (pending.payload as { decision: string; reason: string }).reason !== reviewReason.trim()))} onClick={() => decide(entry, 'Approved')}>Approve locally</button>
          <button type="button" disabled={busy || !reviewReason.trim() || (pending !== null && (pending.kind !== 'review' || pending.id !== entry.explanationId || (pending.payload as { decision: string; reason: string }).decision !== 'Rejected' || (pending.payload as { decision: string; reason: string }).reason !== reviewReason.trim()))} onClick={() => decide(entry, 'Rejected')}>Reject locally</button>
        </>}
      </div> : <div>
        <label>Reason code <input maxLength={100} value={reasonCode} disabled={busy} onChange={event => setReasonCode(event.target.value)} /></label>
        <label>Explanation <textarea maxLength={2000} value={explanation} disabled={busy} onChange={event => setExplanation(event.target.value)} /></label>
        <button type="button" disabled={busy || (pending !== null && (pending.kind !== 'submit' || pending.id !== interval.position || (pending.payload as { reasonCode: string; explanation: string }).reasonCode !== reasonCode.trim() || (pending.payload as { reasonCode: string; explanation: string }).explanation !== explanation.trim())) || !reasonCode.trim() || !explanation.trim()} onClick={submit}>Submit local explanation</button>
      </div>;
    })()}
  </section>;
}
