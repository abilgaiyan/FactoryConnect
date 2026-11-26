import { useEffect, useState } from "react";
import { isCommissioningSnapshot, type CommissioningField } from "./CommissioningPage.tsx";

function group(fields: CommissioningField[], pattern: RegExp) {
  const rows = new Map<string, Record<string, string | null>>();
  for (const field of fields) {
    const match = pattern.exec(field.path);
    if (!match) continue;
    const key = match[1]!, name = match[2]!;
    const row = rows.get(key) ?? {};
    row[name] = field.value;
    rows.set(key, row);
  }
  return [...rows.entries()];
}

export function ScheduleMaster() {
  const [fields, setFields] = useState<CommissioningField[] | null>(null);
  const [error, setError] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setFields(null); setError(false);
    void (async () => {
      try {
        const response = await fetch("/api/commissioning/v1/snapshot", { signal: controller.signal, cache: "no-store", redirect: "error" });
        if (!response.ok) throw Error("Unavailable");
        const data: unknown = await response.json();
        if (!isCommissioningSnapshot(data) || data.configuration.status !== "captured-file-only") throw Error("Unavailable");
        if (!controller.signal.aborted) setFields(data.configuration.fields);
      } catch { if (!controller.signal.aborted) setError(true); }
    })();
    return () => controller.abort();
  }, [attempt]);
  const shifts = group(fields ?? [], /^(ProductionProcessing:ShiftSchedules:\d+):([^:]+)$/);
  const breaks = group(fields ?? [], /^(ProductionProcessing:(?:Machines:\d+:)?PlannedProduction(?:Assignments)?(?::\d+)?:BreakWindows:\d+):([^:]+)$/);
  return <section aria-label="Shift and break master">
    <h2>Shift and break master</h2>
    <p>Read-only captured configuration. These values do not establish runtime activation or historical roster coverage.</p>
    <button type="button" onClick={() => setAttempt(value => value + 1)}>Refresh schedules</button>
    {error ? <p role="alert">Captured schedule configuration unavailable.</p> : fields === null ? <p role="status">Loading schedules…</p> : <>
      <h3>Shift assignments</h3>
      {shifts.length === 0 ? <p>No shift assignments returned.</p> : <div className="activity-table-scroll"><table><thead><tr><th>Shift</th><th>Assignment</th><th>Site / line</th><th>Local start</th><th>Local end</th><th>Time zone</th><th>Effective from / to</th></tr></thead><tbody>
      {shifts.map(([key, row]) => <tr key={key}><td>{row.Name ?? row.ShiftId ?? "Unknown"}</td><td>{row.AssignmentId ?? "Unknown"}</td><td>{row.SiteId ?? "Unknown"} / {row.ProductionLineId ?? "Not specified"}</td><td>{row.StartsAtLocal ?? "Unknown"}</td><td>{row.EndsAtLocal ?? "Unknown"}</td><td>{row.TimeZoneId ?? "Unknown"}</td><td>{row.EffectiveFrom ?? "Unknown"} / {row.EffectiveTo ?? "Not specified"}</td></tr>)}
      </tbody></table></div>}
      <h3>Configured break windows</h3>
      {breaks.length === 0 ? <p>No explicit break windows returned. This does not establish zero planned breaks.</p> : <div className="activity-table-scroll"><table><thead><tr><th>Configuration scope</th><th>Local start</th><th>Local end</th><th>Day offset</th></tr></thead><tbody>
      {breaks.map(([key, row]) => <tr key={key}><td>{key}</td><td>{row.StartsAtLocal ?? "Unknown"}</td><td>{row.EndsAtLocal ?? "Unknown"}</td><td>{row.StartDayOffset ?? "Not specified"}</td></tr>)}
      </tbody></table></div>}
      <p>Break windows are not automatically attributed to a shift on this screen. Editing and activation are not yet available.</p>
    </>}
  </section>;
}
