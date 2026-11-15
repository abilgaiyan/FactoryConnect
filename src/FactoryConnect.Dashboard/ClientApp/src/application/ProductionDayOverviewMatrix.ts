import { createElement, type ReactElement } from "react";

import { OeeExplanation } from "./OeeExplanation.ts";
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
      model.groups.length > 1 ? createElement("h2", null, group.groupName ?? "Ungrouped") : null,
      createElement("p", { "aria-label": "OEE reporting coverage" },
        (() => {
          const calculated = group.machines.filter(machine => machine.metrics.oee.kind === "calculated").length;
          const incomplete = group.machines.length - calculated;
          return "OEE reporting: " + calculated + " of " + group.machines.length +
            " machines calculated; " + incomplete + " awaiting qualified reporting evidence.";
        })()),

      createElement("p", { "aria-label": "OEE evidence gaps by component" },
        (() => {
          const count = (key: "availability" | "performance" | "quality") =>
            group.machines.filter(machine => machine.metrics[key].kind !== "calculated").length;
          return "Evidence gaps — Availability: " + count("availability") +
            "; Performance: " + count("performance") +
            "; Quality: " + count("quality") + ".";
        })()),
      createElement("section", { "aria-label": "Machine OEE comparison" },
        createElement("h3", null, "Machine OEE comparison"),
        ...group.machines.map(machine => {
          const metric = machine.metrics.oee;
          const ratio = metric.kind === "calculated" && metric.unit.toLowerCase() === "ratio"
            ? Number(metric.value) : NaN;
          const valid = Number.isFinite(ratio) && ratio >= 0 && ratio <= 1;
          return createElement("div", {
            key: JSON.stringify([machine.processorId, machine.machineId]),
            style: { display: "grid", gridTemplateColumns: "minmax(8rem, 1fr) minmax(8rem, 3fr) auto", gap: "0.75rem", alignItems: "center", marginBottom: "0.5rem" },
          },
            createElement("span", null, machine.displayName),
            valid
              ? createElement("progress", { max: 1, value: ratio, "aria-label": "OEE for " + machine.displayName, style: { width: "100%" } })
              : createElement("span", null, "No qualified numeric OEE"),
            createElement("span", null, valid && metric.kind === "calculated"
              ? formatCalculatedMetric(metric) : "—"));
        })),
      createElement(
        "table",
        null,
        createElement("caption", null, model.groups.length > 1 ? `Production-day operational metrics for ${group.groupName ?? "ungrouped machines"}` : "Production-day operational metrics"),
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
          ...group.machines.flatMap((machine) => {
            const key = JSON.stringify([machine.processorId, machine.machineId]);
            return [
              createElement(
                "tr",
                { key },
                createElement("th", { scope: "row" }, machine.displayName),
                metricCell(machine.metrics.availability),
                metricCell(machine.metrics.utilization),
                metricCell(machine.metrics.performance),
                metricCell(machine.metrics.quality),
                metricCell(machine.metrics.oee),
              ),
              createElement("tr", { key: `${key}:details`, className: "oee-explanation-row" },
                createElement("td", { colSpan: 6, style: { padding: "0.75rem 1rem" } },
                  createElement(OeeExplanation, { machine, productionDay: model.productionDay }))),
            ];
          }),
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
