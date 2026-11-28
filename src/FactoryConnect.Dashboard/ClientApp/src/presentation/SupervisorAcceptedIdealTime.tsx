import { useEffect, useState } from "react";

type Scope = { machineId: string; shiftScheduleAssignmentId: string; shiftId: string; startsAtUtc: string; endsAtUtc: string };
type Preview = { publicationId: string | null; cycleRevisionId: string | null; idealProductionSeconds: number | null; status: string; cycleSource?: string | null; defaultSnapshotId?: string | null };
const isId = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";

export function SupervisorAcceptedIdealTime({ scope }: { scope: Scope }) {
  const query = new URLSearchParams({assignmentId: scope.shiftScheduleAssignmentId, shiftId: scope.shiftId,
    shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc});
  const url = "/api/supervisor/v1/machines/" + encodeURIComponent(scope.machineId) + "/accepted-ideal-time?" + query;
  const [refresh, setRefresh] = useState(0);
  const [state, setState] = useState<{url: string; refresh: number; preview?: Preview; error?: string} | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void fetch(url, {signal: controller.signal, cache: "no-store", redirect: "error"}).then(async response => {
      if (!response.ok) throw new Error("Accepted ideal time unavailable (HTTP " + response.status + ")");
      const data = await response.json() as Preview;
      const valid = data && (data.status === "accepted-quantity-preview"
        ? isId(data.publicationId) && (data.cycleSource === "accepted-override" ? isId(data.cycleRevisionId) && data.defaultSnapshotId == null :
          ["operation-default", "part-default"].includes(data.cycleSource ?? "") && isId(data.defaultSnapshotId) && data.cycleRevisionId === null) &&
          typeof data.idealProductionSeconds === "number" && Number.isFinite(data.idealProductionSeconds) && data.idealProductionSeconds >= 0
        : ["publication-unavailable", "publication-inapplicable", "cycle-unmatched"].includes(data.status) &&
          data.idealProductionSeconds === null && data.cycleRevisionId === null &&
          (data.status === "publication-unavailable" ? data.publicationId === null : isId(data.publicationId)));
      if (!valid) throw new Error("Invalid accepted ideal time response");
      if (!controller.signal.aborted) setState({url, refresh, preview: data});
    }).catch(error => { if (!controller.signal.aborted) setState({url, refresh, error: String(error)}); });
    return () => controller.abort();
  }, [url, refresh]);
  const current = state?.url === url && state.refresh === refresh ? state : null;
  const preview = current?.preview;
  return <section aria-label="Accepted ideal production time">
    <h3>Accepted ideal production time</h3>
    <p>Accepted quantity × reviewed correction or captured master default. Diagnostic only; not qualified Performance or OEE.</p>
    <button type="button" onClick={() => setRefresh(n => n + 1)}>Refresh accepted ideal time</button>
    {current?.error ? <p role="alert">{current.error}</p> : !preview ? <p role="status">Loading accepted ideal time…</p> :
      preview.status === "accepted-quantity-preview" && preview.idealProductionSeconds !== null ? <>
        <p><strong>{(preview.idealProductionSeconds / 60).toFixed(2)} minutes</strong></p>
        <p>Publication {preview.publicationId} · {preview.cycleSource === "accepted-override" ? `Accepted cycle revision ${preview.cycleRevisionId}` : `${preview.cycleSource === "operation-default" ? "Operation" : "Part"} default snapshot ${preview.defaultSnapshotId}`}</p>
      </> : <p role="status">Unavailable: {preview.status === "publication-unavailable" ? "No accepted publication" :
        preview.status === "cycle-unmatched" ? "No eligible effective cycle" : "Publication context or unit not applicable"}. This does not mean zero production.</p>}
  </section>;
}
