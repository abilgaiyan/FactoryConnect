import { useEffect, useState } from "react";

type Scope = { machineId: string; shiftScheduleAssignmentId: string; shiftId: string; startsAtUtc: string; endsAtUtc: string };
type Entry = { entryId: string; productionOrder: string; partNumber: string; operation: string };
type Item = { entryId: string; cycleRevisionId: string | null; declaredTotalQuantity: number | null; idealProductionSeconds: number | null; status: string };
type Preview = { status: string; potentiallyTruncated: boolean; items: Item[] };

export function SupervisorDeclaredIdealTime({scope, entries}: {scope: Scope; entries: readonly Entry[]}) {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const query = new URLSearchParams({assignmentId: scope.shiftScheduleAssignmentId, shiftId: scope.shiftId, shiftStartsAtUtc: scope.startsAtUtc, shiftEndsAtUtc: scope.endsAtUtc}).toString();
  const url = "/api/supervisor/v1/machines/" + encodeURIComponent(scope.machineId) + "/declared-ideal-time?" + query;
  useEffect(() => {
    const controller = new AbortController();
    setPreview(null); setError("");
    void fetch(url, {signal: controller.signal, cache: "no-store", redirect: "error"}).then(async response => {
      if (!response.ok) throw new Error("Ideal time preview unavailable (HTTP " + response.status + ")");
      const data = await response.json() as Preview;
      if (!data || data.status !== "original-declarations-only" || typeof data.potentiallyTruncated !== "boolean" ||
          !Array.isArray(data.items) || !data.items.every(i => i && typeof i.entryId === "string" &&
          (i.cycleRevisionId === null || typeof i.cycleRevisionId === "string") &&
          (i.declaredTotalQuantity === null || (Number.isSafeInteger(i.declaredTotalQuantity) && i.declaredTotalQuantity >= 0)) &&
          (i.idealProductionSeconds === null || (Number.isFinite(i.idealProductionSeconds) && i.idealProductionSeconds >= 0)) &&
          ["declared-only", "quantity-unavailable", "cycle-unmatched"].includes(i.status))) throw new Error("Invalid ideal time preview");
      if (!controller.signal.aborted) setPreview(data);
    }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [url, entries, refresh]);
  return <section aria-label="Declared ideal production time">
    <h3>Declared ideal production time</h3>
    <p>Original declarations only; corrections and reconciliation are not included. Not qualified OEE. Reload after changing cycle times.</p>
    <button type="button" onClick={() => setRefresh(n => n + 1)}>Refresh ideal time preview</button>
    {error ? <p role="alert">{error}</p> : !preview ? <p>Loading ideal time preview…</p> : <>
      {preview.potentiallyTruncated && <p role="alert">Preview may be incomplete (100-entry limit).</p>}
      <table><thead><tr><th>Production order / part / operation</th><th>Declared pieces</th><th>Ideal minutes</th><th>Status</th></tr></thead>
      <tbody>{entries.map(entry => {
        const matches = preview.items.filter(i => i.entryId === entry.entryId);
        const item = matches.length === 1 ? matches[0] : null;
        return <tr key={entry.entryId}><td>{entry.productionOrder} / {entry.partNumber} / {entry.operation}</td>
          <td>{item?.declaredTotalQuantity ?? "Unavailable"}</td>
          <td>{item?.idealProductionSeconds != null ? (item.idealProductionSeconds / 60).toFixed(2) : "Unavailable"}</td>
          <td>{item?.status === "declared-only" ? "Declared only" : item?.status === "cycle-unmatched" ? "No matching cycle" : "Unavailable"}</td></tr>;
      })}</tbody></table>
    </>}
  </section>;
}
