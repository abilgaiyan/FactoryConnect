import { useEffect, useMemo, useState, type FormEvent } from "react";
import type { DashboardApplicationRuntime } from "../application/application-runtime.ts";
import { isProductionDayIdentity } from "../application/production-day-shift-reporting.ts";
import { useShiftPerformanceOverview } from "../application/use-shift-performance-overview.ts";
import { formatActivityDuration, loadActivityReport, type ActivityReport } from "../application/machine-activity-report.ts";
import { ShiftPerformancePageStateView } from "./ShiftPerformancePageStateView.tsx";
import { ShiftPerformanceOverviewView } from "./ShiftPerformanceOverviewView.tsx";
import type { ShiftPerformanceMachine, ShiftPerformanceOverview, ShiftPerformanceShift } from "./shift-performance-model.ts";
import { MachineActivityEvidence } from "./MachineActivityEvidence.tsx";
import { SupervisorLocalEntryForm } from "./SupervisorLocalEntryForm.tsx";
import { SupervisorLocalShiftTargets } from "./SupervisorLocalShiftTargets.tsx";
import { SupervisorQuantitySourceInspection } from "./SupervisorQuantitySourceInspection.tsx";
import { SupervisorAcceptedProductionReadout } from "./SupervisorAcceptedProductionReadout.tsx";
import { SupervisorTargetAchievement } from "./SupervisorTargetAchievement.tsx";

export function occurrenceKey(machine: ShiftPerformanceMachine, occurrence: ShiftPerformanceShift): string {
  return JSON.stringify([machine.machineId, machine.processorId, machine.siteId, machine.productionLineId,
    occurrence.shift.shiftScheduleAssignmentId, occurrence.shift.shiftId, occurrence.shift.startsAtUtc, occurrence.shift.endsAtUtc]);
}

function exportShiftOeeCsv(overview: ShiftPerformanceOverview, machine: ShiftPerformanceMachine, occurrence: ShiftPerformanceShift) {
  exportOeeCsv(overview, [{ machine, occurrence }], "supervisor-shift-oee.csv");
}

function exportOeeCsv(overview: ShiftPerformanceOverview,
  occurrences: ReadonlyArray<{ machine: ShiftPerformanceMachine; occurrence: ShiftPerformanceShift }>, filename: string) {
  const cell = (value: string) => {
    const safe = /^\s*[=+@-]/.test(value) ? "'" + value : value;
    return '"' + safe.replace(/"/g, '""') + '"';
  };
  const columns = ["Production day", "Machine ID", "Reporting processor", "Site ID", "Shift ID", "Shift assignment", "Shift start UTC", "Shift end UTC", "Metric", "Status", "Value", "Unit", "Reason code", "Reason operand"];
  const rows = occurrences.flatMap(({ machine, occurrence }) => {
    const base = [overview.productionDay, machine.machineId, machine.processorId, machine.siteId, occurrence.shift.shiftId, occurrence.shift.shiftScheduleAssignmentId, occurrence.shift.startsAtUtc, occurrence.shift.endsAtUtc];
    return ([
      ["Availability", occurrence.availability], ["Performance", occurrence.performance],
      ["Quality", occurrence.quality], ["OEE", occurrence.oee],
    ] as const).map(([name, metric]) => [...base, name, metric.state,
      metric.state === "calculated" ? String(metric.value) : "",
      metric.state === "calculated" ? metric.unit : "",
      metric.state === "unavailable" || metric.state === "insufficient-evidence" ? metric.reasonCode ?? "" : "",
      metric.state === "unavailable" || metric.state === "insufficient-evidence" ? metric.reasonOperandName ?? "" : "",
    ]);
  });
  const csv = [columns, ...rows].map(row => row.map(cell).join(",")).join("\r\n");
  const href = URL.createObjectURL(new Blob(["\uFEFF", csv], { type: "text/csv;charset=utf-8" }));
  const anchor = document.createElement("a");
  anchor.href = href;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(href);
}

export function SupervisorReadOnly({ runtime }: { readonly runtime: DashboardApplicationRuntime }) {
  const [sourceIndex, setSourceIndex] = useState("");
  const [day, setDay] = useState("");
  const [error, setError] = useState("");
  const [request, setRequest] = useState<{ sourceIndex: number; day: string; generation: number } | null>(null);
  const [generation, setGeneration] = useState(0);
  function submit(event: FormEvent) {
    event.preventDefault(); setRequest(null);
    if (sourceIndex === "" || !runtime.configuration.sources[Number(sourceIndex)] || !isProductionDayIdentity(day)) {
      setError("Select one configured machine / processor and a valid production business date."); return;
    }
    setError(""); setGeneration(value => value + 1);
    setRequest({ sourceIndex: Number(sourceIndex), day, generation: generation + 1 });
  }
  return <>
    <form onSubmit={submit} className="supervisor-controls">
      <label>Machine / reporting processor<select value={sourceIndex} onChange={event => { setSourceIndex(event.target.value); setRequest(null); setError(""); }}>
        <option value="">Select a configured source</option>
        {runtime.configuration.sources.map((source, index) => <option key={index} value={index}>{source.displayName} · {source.siteId} · {source.productionLineId} · {source.processorId}</option>)}
      </select></label>
      <label>Production business date<input type="date" value={day} onChange={event => { setDay(event.target.value); setRequest(null); setError(""); }} /></label>
      <button type="submit">Load shift occurrences</button>
    </form>
    {!runtime.configuration.sources.length && <p>No configured reporting sources. No sample machines have been substituted.</p>}
    {error && <p role="alert">{error}</p>}
    {request && <ShiftRead key={`${request.sourceIndex}:${request.day}:${request.generation}`} runtime={runtime} sourceIndex={request.sourceIndex} day={request.day} />}
  </>;
}

function ShiftRead({ runtime, sourceIndex, day }: { readonly runtime: DashboardApplicationRuntime; readonly sourceIndex: number; readonly day: string }) {
  const scopedRuntime = useMemo(() => ({ ...runtime, configuration: { ...runtime.configuration, sources: [runtime.configuration.sources[sourceIndex]!] } }), [runtime, sourceIndex]);
  const { state } = useShiftPerformanceOverview(day, scopedRuntime);
  return state.kind === "success" && !state.isRefreshing
    ? <OccurrenceRead key={day} overview={state.overview} runtime={scopedRuntime} />
    : <ShiftPerformancePageStateView state={state} />;
}

function OccurrenceRead({ overview, runtime }: { readonly overview: ShiftPerformanceOverview; readonly runtime: DashboardApplicationRuntime }) {
  const [selectedKey, setSelectedKey] = useState("");
  const [activeView, setActiveView] = useState<"overview" | "production" | "targets" | "activity" | "diagnostics">("overview");
  const occurrences = overview.groups.flatMap(group => group.machines.flatMap(machine => machine.shifts.map(occurrence => ({ machine, occurrence }))));
  const selected = occurrences.find(({ machine, occurrence }) => occurrenceKey(machine, occurrence) === selectedKey);
  const oeeBlockers = new Map<string, { count: number; firstOccurrenceKey: string }>();
  for (const { machine, occurrence } of occurrences) {
    if (occurrence.oee.state === "calculated") continue;
    const reasons = [occurrence.performance, occurrence.quality, occurrence.availability]
      .flatMap(metric => metric.state === "insufficient-evidence" || metric.state === "unavailable"
        ? [metric.reasonCode?.trim()].filter((reason): reason is string => Boolean(reason)) : []);
    const distinct = new Set(reasons.length ? reasons : [occurrence.oee.state === "missing" ? "OEE not returned" : occurrence.oee.reasonCode?.trim() || "Reason not provided"]);
    for (const reason of distinct) {
      const current = oeeBlockers.get(reason);
      oeeBlockers.set(reason, { count: (current?.count ?? 0) + 1, firstOccurrenceKey: current?.firstOccurrenceKey ?? occurrenceKey(machine, occurrence) });
    }
  }
  return <>
    <p role="status">{occurrences.length} recorded shift occurrence(s) returned for {overview.productionDay}. Select one to inspect its metrics and activity.</p>
    {occurrences.length > 0 && <button type="button" onClick={() => exportOeeCsv(overview, occurrences, "supervisor-production-day-oee.csv")}>Export production-day OEE evidence (CSV)</button>}
    {occurrences.length > 0 && <p>The production-day export includes all returned machine/shift occurrences, their metric statuses and evidence reasons. Missing values are blank, never zero.</p>}
    {occurrences.length > 0 && <section aria-label="Shift OEE evidence coverage" className="supervisor-detail">
      <h2>OEE readiness for this production day</h2>
      <p><strong>{occurrences.filter(({ occurrence }) => occurrence.oee.state === "calculated").length}</strong> of <strong>{occurrences.length}</strong> shift occurrences have a reported calculated OEE.</p>
      <p>{occurrences.filter(({ occurrence }) => occurrence.oee.state === "insufficient-evidence").length} insufficient evidence · {occurrences.filter(({ occurrence }) => occurrence.oee.state === "unavailable").length} unavailable · {occurrences.filter(({ occurrence }) => occurrence.oee.state === "missing").length} not returned.</p>
      {oeeBlockers.size > 0 && <div aria-label="OEE evidence blockers">
        <h3>Evidence gaps to resolve</h3>
        <ul>{[...oeeBlockers.entries()].sort((a, b) => b[1].count - a[1].count || a[0].localeCompare(b[0])).map(([reason, blocker]) =>
          <li key={reason}><button type="button" onClick={() => { setSelectedKey(blocker.firstOccurrenceKey); setActiveView("overview"); }}>
            {reason}: {blocker.count} shift occurrence(s) · Inspect example
          </button></li>)}</ul>
        <p>Select a reason to inspect one affected shift. A shift may have more than one evidence gap. These are reporting reason codes, not inferred production quantities.</p>
      </div>}
      <p>Counts are per exact shift, not an average or a production total. Select a shift to inspect its evidence and reasons.</p>
    </section>}
    {occurrences.length > 0 && <div className="supervisor-shift-choices" aria-label="Available shift occurrences">
      {occurrences.map(({ machine, occurrence }) => {
        const key = occurrenceKey(machine, occurrence);
        return <button type="button" key={key} aria-pressed={selectedKey === key} onClick={() => { setSelectedKey(key); setActiveView("overview"); }}>
          {occurrence.shift.shiftId} · {occurrence.shift.startsAtUtc}
          {" · OEE: "}{occurrence.oee.state === "calculated" ? "Calculated" : occurrence.oee.state === "insufficient-evidence" ? "Insufficient evidence" : occurrence.oee.state === "unavailable" ? "Unavailable" : "Missing"}
        </button>;
      })}
    </div>}
    <label>Exact shift occurrence<select value={selectedKey} onChange={event => { setSelectedKey(event.target.value); setActiveView("overview"); }}>
      <option value="">Select an exact occurrence</option>
      {occurrences.map(({ machine, occurrence }) => <option key={occurrenceKey(machine, occurrence)} value={occurrenceKey(machine, occurrence)}>
        {occurrence.shift.shiftId} · {occurrence.shift.shiftScheduleAssignmentId} · {occurrence.shift.startsAtUtc} – {occurrence.shift.endsAtUtc}
      </option>)}
    </select></label>
    {!occurrences.length && <p>No authoritative shift occurrences returned. This does not establish zero production.</p>}
    {selected && <section className="supervisor-selected-shift" aria-label="Selected shift workspace">
      <p className="supervisor-shift-summary">Production business date <strong>{overview.productionDay}</strong> · Shift <strong>{selected.occurrence.shift.shiftId}</strong> · {selected.occurrence.shift.startsAtUtc} – {selected.occurrence.shift.endsAtUtc} (UTC)</p>
      <nav className="supervisor-workspace-tabs" aria-label="Selected shift sections">
        <button type="button" aria-pressed={activeView === "overview"} onClick={() => setActiveView("overview")}>OEE overview</button>
        <button type="button" aria-pressed={activeView === "production"} onClick={() => setActiveView("production")}>Production entry</button>
        <button type="button" aria-pressed={activeView === "targets"} onClick={() => setActiveView("targets")}>Shift targets</button>
        <button type="button" aria-pressed={activeView === "activity"} onClick={() => setActiveView("activity")}>Machine activity</button>
        <button type="button" aria-pressed={activeView === "diagnostics"} onClick={() => setActiveView("diagnostics")}>Technical details</button>
      </nav>
      <div hidden={activeView !== "overview"}>
        <section aria-label="Shift target navigation" className="supervisor-detail">
          <h2>Production plan and actual evidence</h2>
          <SupervisorLocalShiftTargets key={`overview-target:${selectedKey}`} summaryOnly scope={{
            machineId: selected.machine.machineId,
            shiftScheduleAssignmentId: selected.occurrence.shift.shiftScheduleAssignmentId,
            shiftId: selected.occurrence.shift.shiftId,
            startsAtUtc: selected.occurrence.shift.startsAtUtc,
            endsAtUtc: selected.occurrence.shift.endsAtUtc
          }} />
          <p>SQL planning target, accepted actual, and qualified target achievement are shown separately. Target achievement is not OEE Performance.</p>
          <section aria-label="Actual production evidence readiness">
            <h3>Actual production evidence readiness</h3>
            <dl>
              <dt>Performance evidence</dt>
              <dd>{selected.occurrence.performance.state === "calculated" ? "Reporting calculated Performance; quantity completeness must still be verified separately" :
                selected.occurrence.performance.state === "missing" ? "No Performance metric returned" :
                selected.occurrence.performance.state === "insufficient-evidence" ? "Insufficient evidence" : "Unavailable"}
                {selected.occurrence.performance.state !== "calculated" && selected.occurrence.performance.state !== "missing" &&
                  selected.occurrence.performance.reasonCode ? ` — ${selected.occurrence.performance.reasonCode}` : ""}</dd>
              <dt>Quality evidence</dt>
              <dd>{selected.occurrence.quality.state === "calculated" ? "Reporting calculated Quality; good/rejected quantity coverage must still be verified separately" :
                selected.occurrence.quality.state === "missing" ? "No Quality metric returned" :
                selected.occurrence.quality.state === "insufficient-evidence" ? "Insufficient evidence" : "Unavailable"}
                {selected.occurrence.quality.state !== "calculated" && selected.occurrence.quality.state !== "missing" &&
                  selected.occurrence.quality.reasonCode ? ` — ${selected.occurrence.quality.reasonCode}` : ""}</dd>
              <dt>Verified produced quantity</dt><dd>Not supplied by this reporting response; no actual quantity or attainment percentage calculated</dd>
            </dl>
          </section>
          <SupervisorAcceptedProductionReadout key={`accepted:${selectedKey}`} machine={selected.machine} shift={selected.occurrence} />
          <SupervisorTargetAchievement key={`achievement:${selectedKey}`} machine={selected.machine} shift={selected.occurrence} />
          <SupervisorQuantitySourceInspection key={`quantity:${selectedKey}`} machine={selected.machine} shift={selected.occurrence} />
          <button type="button" onClick={() => setActiveView("targets")}>View current shift target</button>
          <button type="button" onClick={() => setActiveView("production")}>View supervisor production entries</button>
        </section>
        <h2>Shift OEE performance</h2>
        <button type="button" onClick={() => exportShiftOeeCsv(overview, selected.machine, selected.occurrence)}>Export exact-shift OEE evidence (CSV)</button>
        <p>The export contains reported values, statuses and reasons only. Missing metrics remain blank, not zero.</p>
        <ShiftPerformanceOverviewView key={`metrics:${selectedKey}`} showFilters={false} overview={{ productionDay: overview.productionDay, groups: [{ groupName: null, machines: [{ ...selected.machine, shifts: [selected.occurrence] }] }] }} />
        <p>Only qualified metrics are numeric. Missing production quantities, reference time, or quality evidence remain explicitly unavailable.</p>
        <section aria-label="OEE evidence checklist">
          <h3>OEE evidence checklist</h3>
          <p>These are the reporting service's current evidence states for this exact shift, not a request to enter estimated production.</p>
          <ul>
            {([["Availability", selected.occurrence.availability], ["Performance", selected.occurrence.performance], ["Quality", selected.occurrence.quality], ["OEE", selected.occurrence.oee]] as const).map(([name, metric]) => (
              <li key={name}><strong>{name}:</strong> {metric.state === "calculated" ? "Qualified numeric result" : metric.state === "missing" ? "Not returned by reporting" : metric.state === "insufficient-evidence" ? "Insufficient evidence" : "Unavailable"}
                {metric.state !== "calculated" && metric.state !== "missing" && metric.reasonCode && <> — {metric.reasonCode}{metric.reasonOperandName ? ` (${metric.reasonOperandName})` : ""}</>}
              </li>
            ))}
          </ul>
          <details>
            <summary>How OEE is calculated and what evidence is required</summary>
            <dl>
              <dt>Availability</dt><dd>Actual production time ÷ planned operating time. Requires qualified activity and planned-time evidence.</dd>
              <dt>Performance</dt><dd>Ideal production duration ÷ actual production time. Requires qualified production quantity and applicable production reference time.</dd>
              <dt>Quality</dt><dd>Good quantity ÷ total quantity. Requires qualified good and total counts with a consistent production population.</dd>
              <dt>OEE</dt><dd>Availability × Performance × Quality. The report must establish all three components before numeric OEE is shown.</dd>
            </dl>
            <p>These are formula explanations, not recomputed values. Missing evidence is not zero, and a calculated status alone does not establish complete historical coverage.</p>
          </details>
          <p>Production entry is a local commissioning workflow. Saved or locally approved declarations do not automatically become accepted CNC quantities or qualify OEE. Resolve missing reference-time, production quantity and quality evidence through the separate qualification process.</p>
        </section>
      </div>
      <div hidden={activeView !== "production"}><SupervisorLocalEntryForm key={`entries:${selectedKey}`} scope={{ machineId: selected.machine.machineId,
        shiftScheduleAssignmentId: selected.occurrence.shift.shiftScheduleAssignmentId,
        shiftId: selected.occurrence.shift.shiftId,
        startsAtUtc: selected.occurrence.shift.startsAtUtc,
        endsAtUtc: selected.occurrence.shift.endsAtUtc }} /></div>
      {activeView === "targets" && <div><SupervisorLocalShiftTargets key={`targets:${selectedKey}`} scope={{
        machineId: selected.machine.machineId,
        shiftScheduleAssignmentId: selected.occurrence.shift.shiftScheduleAssignmentId,
        shiftId: selected.occurrence.shift.shiftId,
        startsAtUtc: selected.occurrence.shift.startsAtUtc,
        endsAtUtc: selected.occurrence.shift.endsAtUtc
      }} /></div>}
      <div hidden={activeView !== "activity"}><ExactActivity key={`activity:${selectedKey}`} machine={selected.machine} occurrence={selected.occurrence} timeoutMilliseconds={runtime.configuration.requestTimeoutMilliseconds} /></div>
      <div hidden={activeView !== "diagnostics"}><section aria-label="Reporting evidence provenance">
        <h2>Reporting evidence provenance</h2>
        <p>Site {selected.machine.siteId} · Line {selected.machine.productionLineId}</p>
        <p>Assignment {selected.occurrence.shift.shiftScheduleAssignmentId}</p>
        <dl>
          <dt>Machine ID</dt><dd><code>{selected.machine.machineId}</code></dd>
          <dt>Reporting processor</dt><dd><code>{selected.machine.processorId}</code></dd>
          <dt>Shift assignment</dt><dd><code>{selected.occurrence.shift.shiftScheduleAssignmentId}</code></dd>
          <dt>Reporting source revision</dt><dd><code>{selected.occurrence.sourceRevision === null ? "Not returned" : JSON.stringify(selected.occurrence.sourceRevision)}</code></dd>
        </dl>
        <p>Metric evidence read time is not provided. Metrics and activity use independent reads and revision domains; no common atomic snapshot is claimed.</p>
      </section></div>
    </section>}
  </>;
}

function ExactActivity({ machine, occurrence, timeoutMilliseconds }: { readonly machine: ShiftPerformanceMachine; readonly occurrence: ShiftPerformanceShift; readonly timeoutMilliseconds: number }) {
  const [result, setResult] = useState<ActivityReport | null>(null);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let current = true;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMilliseconds);
    setResult(null); setError("");
    void loadActivityReport(machine.machineId, { fromInclusive: occurrence.shift.startsAtUtc, toExclusive: occurrence.shift.endsAtUtc }, controller.signal).then(report => {
      if (current && !controller.signal.aborted) setResult(report);
    }, () => {
      if (current) setError(controller.signal.aborted ? "Activity request timed out; no activity evidence established." : "Activity read unavailable or invalid; no activity evidence established.");
    }).finally(() => clearTimeout(timeout));
    return () => { current = false; controller.abort(); clearTimeout(timeout); };
  }, [machine.machineId, occurrence.shift.startsAtUtc, occurrence.shift.endsAtUtc, timeoutMilliseconds, attempt]);
  return <section className="activity-report" aria-label="Exact shift activity">
    <h2>Recorded activity · read only</h2>
    <p>Closed stored periods only. Displayed bounds may be clipped to this occurrence; original source bounds remain visible. Open periods, missing coverage and recorded Unknown are different conditions.</p>
    <p>Durable explanation actions are unavailable. The API does not return the complete publication revision/output ordinal reference. Positions are inspection provenance, not a replacement exact-event key.</p>
    {error ? <><p role="alert">{error} No sample fallback.</p><button type="button" onClick={() => setAttempt(value => value + 1)}>Retry activity</button></>
      : result ? <><p>Unrepresented coverage: {formatActivityDuration(result.unrepresentedTicks)}. Not proof of downtime or acquisition failure.</p><MachineActivityEvidence report={result} /></>
      : <p role="status">Loading exact shift activity…</p>}
  </section>;
}
