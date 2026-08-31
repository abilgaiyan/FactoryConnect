import type { OperationalMetricPage } from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

export interface ProductionDayMetricResultsProps {
  readonly page: OperationalMetricPage;
  readonly sources: readonly DashboardRuntimeSource[];
}

export function ProductionDayMetricResults({ page, sources }: ProductionDayMetricResultsProps) {
  const sourceNames = new Map(
    sources.map((source) => [`${source.machineId}\u0000${source.processorId}`, source.displayName]),
  );

  return (
    <div>
      <table>
        <caption>Operational metrics returned by the reporting API</caption>
        <thead>
          <tr>
            <th scope="col">Source</th>
            <th scope="col">Metric</th>
            <th scope="col">Version</th>
            <th scope="col">Status</th>
            <th scope="col">Value</th>
            <th scope="col">Unit</th>
            <th scope="col">Reason</th>
          </tr>
        </thead>
        <tbody>
          {page.items.map((item) => {
            const sourceKey = `${item.machineId}\u0000${item.processorId}`;
            const sourceName = sourceNames.get(sourceKey) ?? item.machineId;
            const reason = item.reasonCode === null
              ? "—"
              : item.reasonOperandName === null
                ? item.reasonCode
                : `${item.reasonCode} (${item.reasonOperandName})`;

            return (
              <tr key={`${item.processorId}:${item.machineId}:${item.metricKey}:${item.definitionVersion}:${item.sourceRevision.position}`}>
                <td>{sourceName}</td>
                <td>{item.metricKey}</td>
                <td>{item.definitionVersion}</td>
                <td>{item.status}</td>
                <td>{item.value === null ? "—" : String(item.value)}</td>
                <td>{item.unit}</td>
                <td>{reason}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {page.continuationToken === null ? null : (
        <p>Additional reporting results are available.</p>
      )}
    </div>
  );
}
