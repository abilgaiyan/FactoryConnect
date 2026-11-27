import { useEffect, useState } from "react";

type Scope = { machineId: string; shiftScheduleAssignmentId: string; shiftId: string; startsAtUtc: string; endsAtUtc: string };
type Revision = { cycleRevisionId: string; revisionNumber: number; idealCycleSeconds: number; productionOrder: string | null; partNumber: string | null; operation: string | null };
type Review = { reviewId: string; cycleRevisionId: string; decision: number; reviewedBy: string; reviewedAtUtc: string };
export function SupervisorCycleReviews({scope, history}: {scope: Scope; history: Revision[]}) {
  const [reviews, setReviews] = useState<Review[] | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [pending, setPending] = useState<{cycleRevisionId: string; body: string; accepted: boolean} | null>(null);
  const url = "/api/supervisor/v1/machines/" + encodeURIComponent(scope.machineId) + "/ideal-cycle-reviews";
  const fields = {assignmentId: scope.shiftScheduleAssignmentId, shiftId: scope.shiftId, shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc};
  const query = new URLSearchParams(fields).toString();
  useEffect(() => {
    const controller = new AbortController();
    setReviews(null); setError("");
    void fetch(url + "?" + query, {signal: controller.signal, cache:"no-store", redirect:"error"})
      .then(async response => {
        if (!response.ok) throw new Error("Review history unavailable (HTTP " + response.status + ")");
        const data: unknown = await response.json();
        if (!Array.isArray(data) || !data.every(r => r && typeof r.reviewId === "string" && typeof r.cycleRevisionId === "string" && (r.decision === 0 || r.decision === 1) && typeof r.reviewedBy === "string" && typeof r.reviewedAtUtc === "string") ||
            new Set(data.map(r => r.cycleRevisionId)).size !== data.length) throw new Error("Invalid review history");
        if (!controller.signal.aborted) setReviews(data);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [url, query, refresh]);
  const key = (r: Revision) => JSON.stringify([r.productionOrder,r.partNumber,r.operation]);
  const byId = new Map(reviews?.map(r => [r.cycleRevisionId,r]));
  const accepted = new Map<string, Revision>();
  for (const r of history) if (byId.get(r.cycleRevisionId)?.decision === 0) {
    const previous = accepted.get(key(r));
    if (!previous || previous.revisionNumber < r.revisionNumber) accepted.set(key(r),r);
  }
  async function submit(r: Revision, isAccepted: boolean) {
    if (busy || !reviews || byId.has(r.cycleRevisionId) || (pending && (pending.cycleRevisionId !== r.cycleRevisionId || pending.accepted !== isAccepted))) return;
    const body = pending?.body ?? JSON.stringify({...fields, reviewId:crypto.randomUUID(), cycleRevisionId:r.cycleRevisionId,isAccepted});
    setPending({cycleRevisionId:r.cycleRevisionId,body,accepted:isAccepted}); setBusy(true); setError("");
    try {
      const response = await fetch(url,{method:"POST",headers:{"Content-Type":"application/json"},body,redirect:"error"});
      if (!response.ok) {
        if ([400,401,403,409,422].includes(response.status)) {setPending(null);setRefresh(n=>n+1);}
        throw new Error("Review not confirmed (HTTP " + response.status + ")");
      }
      setPending(null);setRefresh(n=>n+1);
    } catch(e) {setError(String(e));} finally {setBusy(false);}
  }
  return <section aria-label="Ideal cycle reviews">
    <h3>Ideal cycle reviews</h3>
    <p>Accept or reject overrides. Accepted values are not yet connected to qualified OEE.</p>
    <button type="button" disabled={busy} onClick={()=>setRefresh(n=>n+1)}>Reload reviews</button>
    {error && <p role="alert">{error}</p>}
    {!reviews ? <p>Review history not available.</p> : history.map(r => {
      const review = byId.get(r.cycleRevisionId);
      return <div key={r.cycleRevisionId}>
        <p>{r.productionOrder ?? "No order"} · {r.partNumber ?? "No part"} / {r.operation ?? "No operation"} · Revision {r.revisionNumber}: {r.idealCycleSeconds} sec/piece · {review ? review.decision === 1 ? "Rejected" : accepted.get(key(r))?.cycleRevisionId === r.cycleRevisionId ? "Accepted — current" : "Accepted — superseded" : "Pending review"}</p>
        {review && <p>Reviewed by {review.reviewedBy}</p>}
        {!review && <><button type="button" disabled={busy || Boolean(pending && (pending.cycleRevisionId !== r.cycleRevisionId || !pending.accepted))} onClick={()=>void submit(r,true)}>Accept</button>
        <button type="button" disabled={busy || Boolean(pending && (pending.cycleRevisionId !== r.cycleRevisionId || pending.accepted))} onClick={()=>void submit(r,false)}>Reject</button></>}
      </div>;
    })}
    {pending && <p role="alert">Review outcome uncertain. Reload or retry the same decision.</p>}
  </section>;
}
