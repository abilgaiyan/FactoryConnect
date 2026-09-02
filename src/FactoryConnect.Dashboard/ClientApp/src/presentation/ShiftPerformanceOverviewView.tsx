import type {
  PresentedMetric,
  ShiftPerformanceGroup,
  ShiftPerformanceMachine,
  ShiftPerformanceOverview,
  ShiftPerformanceShift,
} from "./shift-performance-model.ts";
import { formatPresentedMetric } from "./shift-performance-view-formatting.ts";

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
  const text = formatPresentedMetric(metric);
  const missingLabel = metric.state === "missing" ? `${metric.metricKey} missing` : undefined;

  return (
    <td aria-label={missingLabel}>
      {metric.state === "unavailable" || metric.state === "insufficient-evidence"
        ? <strong>{text.primary}</strong>
        : text.primary}
      {text.evidence === null ? null : <span> — {text.evidence}</span>}
    </td>
  );
}
