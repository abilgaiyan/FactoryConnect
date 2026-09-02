import type {
  PresentedMetric,
  ShiftPerformanceGroup,
  ShiftPerformanceMachine,
  ShiftPerformanceOverview,
  ShiftPerformanceShift,
} from "./shift-performance-model.ts";
import { formatRatioAsPercentage } from "../application/production-day-metric-formatting.ts";

export interface ShiftPerformanceOverviewViewProps {
  readonly overview: ShiftPerformanceOverview;
}

export function ShiftPerformanceOverviewView({ overview }: ShiftPerformanceOverviewViewProps) {
  return (
    <section aria-labelledby="shift-performance-title">
      <h2 id="shift-performance-title">Shift performance</h2>
      <p>Production day: {overview.productionDay}</p>
      {overview.groups.length === 0
        ? <p>No configured machines.</p>
        : overview.groups.map((group, index) => (
          <ShiftPerformanceGroupView key={`${group.groupName ?? "ungrouped"}-${index}`} group={group} />
        ))}
    </section>
  );
}

function ShiftPerformanceGroupView({ group }: { readonly group: ShiftPerformanceGroup }) {
  return (
    <section aria-label={group.groupName ?? "Ungrouped machines"}>
      <h3>{group.groupName ?? "Ungrouped"}</h3>
      {group.machines.map(machine => (
        <ShiftPerformanceMachineView
          key={`${machine.machineId}\u0000${machine.processorId}`}
          machine={machine}
        />
      ))}
    </section>
  );
}

function ShiftPerformanceMachineView({ machine }: { readonly machine: ShiftPerformanceMachine }) {
  return (
    <section aria-label={machine.displayName}>
      <h4>{machine.displayName}</h4>
      {machine.shifts.length === 0
        ? <p>No authoritative shift occurrences returned.</p>
        : (
          <table>
            <thead>
              <tr>
                <th scope="col">Shift</th>
                <th scope="col">UTC interval</th>
                <th scope="col">Availability</th>
                <th scope="col">Utilization</th>
                <th scope="col">Performance</th>
                <th scope="col">Quality</th>
                <th scope="col">OEE</th>
              </tr>
            </thead>
            <tbody>
              {machine.shifts.map(shift => (
                <ShiftOccurrenceView
                  key={`${shift.shift.shiftScheduleAssignmentId}\u0000${shift.shift.shiftId}\u0000${shift.shift.startsAtUtc}\u0000${shift.shift.endsAtUtc}`}
                  shift={shift}
                />
              ))}
            </tbody>
          </table>
        )}
    </section>
  );
}

function ShiftOccurrenceView({ shift }: { readonly shift: ShiftPerformanceShift }) {
  return (
    <tr>
      <th scope="row">{shift.shift.shiftId}</th>
      <td><time dateTime={shift.shift.startsAtUtc}>{shift.shift.startsAtUtc}</time> – <time dateTime={shift.shift.endsAtUtc}>{shift.shift.endsAtUtc}</time></td>
      <PresentedMetricValue metric={shift.availability} />
      <PresentedMetricValue metric={shift.utilization} />
      <PresentedMetricValue metric={shift.performance} />
      <PresentedMetricValue metric={shift.quality} />
      <PresentedMetricValue metric={shift.oee} />
    </tr>
  );
}

function PresentedMetricValue({ metric }: { readonly metric: PresentedMetric }) {
  switch (metric.state) {
    case "calculated":
      return <td>{metric.unit.toLowerCase() === "ratio" ? `${formatRatioAsPercentage(metric.value)}%` : `${String(metric.value)} ${metric.unit}`}</td>;
    case "unavailable":
      return <td><strong>Unavailable</strong><ReasonEvidence metric={metric} /></td>;
    case "insufficient-evidence":
      return <td><strong>Insufficient evidence</strong><ReasonEvidence metric={metric} /></td>;
    case "missing":
      return <td aria-label={`${metric.metricKey} missing`}>—</td>;
  }
}

function ReasonEvidence({ metric }: { readonly metric: Extract<PresentedMetric, { readonly state: "unavailable" | "insufficient-evidence" }> }) {
  const evidence = [metric.reasonCode, metric.reasonOperandName].filter((value): value is string => value !== null);
  return evidence.length === 0 ? null : <span> — {evidence.join(" / ")}</span>;
}
