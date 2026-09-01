import { createElement, type ReactElement } from "react";

import { formatCalculatedMetric } from "./production-day-metric-formatting.ts";
import type {
  ProductionDayMetricDisplay,
  ProductionDayOverviewModel,
} from "./production-day-presentation.ts";

export interface ProductionDayOverviewMatrixProps {
  readonly model: ProductionDayOverviewModel;
}

export function ProductionDayOverviewMatrix({ model }: ProductionDayOverviewMatrixProps): ReactElement {
  return createElement(
    "div",
    { style: { overflowX: "auto" } },
    ...model.groups.map((group) => createElement(
      "section",
      { key: JSON.stringify([group.groupName]) },
      createElement("h2", null, group.groupName ?? "Ungrouped"),
      createElement(
        "table",
        null,
        createElement("caption", null, `Production-day operational metrics for ${group.groupName ?? "ungrouped machines"}`),
        createElement(
          "thead",
          null,
          createElement(
            "tr",
            null,
            ...["Machine", "Availability", "Utilization", "Performance", "Quality", "OEE"].map((heading) =>
              createElement("th", { key: heading, scope: "col" }, heading),
            ),
          ),
        ),
        createElement(
          "tbody",
          null,
          ...group.machines.map((machine) => createElement(
            "tr",
            { key: JSON.stringify([machine.processorId, machine.machineId]) },
            createElement("th", { scope: "row" }, machine.displayName),
            metricCell(machine.metrics.availability),
            metricCell(machine.metrics.utilization),
            metricCell(machine.metrics.performance),
            metricCell(machine.metrics.quality),
            metricCell(machine.metrics.oee),
          )),
        ),
      ),
    )),
  );
}

function metricCell(metric: ProductionDayMetricDisplay): ReactElement {
  return createElement("td", { key: metric.metricKey }, metricContent(metric));
}

function metricContent(metric: ProductionDayMetricDisplay): ReactElement | string {
  switch (metric.kind) {
    case "calculated":
      return formatCalculatedMetric(metric);
    case "unavailable":
      return stateWithReason("— Unavailable", metric.reasonCode, metric.reasonOperandName);
    case "insufficient-evidence":
      return stateWithReason("— Insufficient evidence", metric.reasonCode, metric.reasonOperandName);
    case "missing":
      return "— Missing";
  }
}

function stateWithReason(
  label: string,
  reasonCode: string | null,
  reasonOperandName: string | null,
): ReactElement {
  const reason = reasonCode === null
    ? null
    : reasonOperandName === null
      ? reasonCode
      : `${reasonCode}: ${reasonOperandName}`;

  return createElement(
    "span",
    null,
    label,
    reason === null ? null : createElement("small", { style: { display: "block" } }, reason),
  );
}
