import { createElement as h, type ReactElement } from "react";
import type { ProductionDayMachineOverview, ProductionDayMetricDisplay } from "./production-day-presentation.ts";

const formulas = {
  availability: "ActualProductionTime / PlannedOperatingTime",
  performance: "IdealProductionDuration / ActualProductionTime",
  quality: "GoodQuantity / TotalQuantity",
  oee: "Availability × Performance × Quality",
} as const;

/** Explains the displayed response only. No requests or metric evaluation. */
export function OeeExplanation({ machine, productionDay }: {
  readonly machine: ProductionDayMachineOverview;
  readonly productionDay: string;
}): ReactElement {
  return h("details", { className: "oee-explanation" },
    h("summary", { "aria-label": `OEE calculation details for ${machine.displayName}` }, "ⓘ OEE details"),
    h("section", { "aria-label": "OEE calculation details", style: { minWidth: "18rem", maxWidth: "40rem", whiteSpace: "normal", overflowWrap: "anywhere" } },
      h("h3", null, "OEE Calculation Details"),
      h("p", null, `${machine.displayName} · ${productionDay} · Production day`),
      h("p", null, `Machine: ${machine.machineId} · Reporting processor: ${machine.processorId}`),
      h("p", null, "Context: unpartitioned. These details belong to the displayed report, including while it is refreshing."),
      ...(["availability", "performance", "quality", "oee"] as const).map(key =>
        metricExplanation(key, machine.metrics[key])),
      h("p", null, "Substituted operand values, underlying evidence references and independent formula-evaluation evidence are not supplied by this reporting response. Calculation agreement has not been checked."),
      h("strong", null, "Evidence verification not established"),
      h("p", null, "A calculated status does not establish historical completeness. Missing values are not zero. OEE requires supported Availability, Performance and Quality results."),
    ));
}

function metricExplanation(key: keyof typeof formulas, metric: ProductionDayMetricDisplay): ReactElement {
  const item = metric.kind === "missing" ? undefined : metric.reportItem;
  const revision = metric.kind === "missing" ? undefined : metric.sourceRevision;
  const position = revision?.position;
  const exactPosition = typeof position === "number" && !Number.isSafeInteger(position)
    ? "Exact identity unavailable (unsafe numeric position)"
    : position === undefined ? "Not supplied" : String(position);
  return h("section", { key },
    h("h4", null, key === "oee" ? "OEE" : key[0]!.toUpperCase() + key.slice(1)),
    h("p", null, `Built-in formula reference (definition ${metric.version}): ${formulas[key]}`),
    h("p", null, metric.kind === "missing"
      ? "Metric not present in the displayed report."
      : `Reported status: ${metric.kind}`),
    h("p", null, `Reported value: ${metric.kind === "calculated" ? `${String(metric.value)} ${metric.unit}` : "—"}`),
    metric.kind === "unavailable" || metric.kind === "insufficient-evidence"
      ? h("p", null, `Reported reason: ${metric.reasonCode ?? "Not supplied"}; operand: ${metric.reasonOperandName ?? "Not supplied"}`) : null,
    item ? h("p", null, `Site: ${item.productionDay?.siteId ?? "Not supplied"}; scope: ${item.scope}; definition: ${item.metricKey}/${item.definitionVersion}; unit: ${item.unit}`) : null,
    revision ? h("p", null, `Aggregation checkpoint: machine ${revision.machineId}; processor ${revision.processorId}; stream ${revision.streamKey}; position ${exactPosition}`) : h("p", null, "Aggregation checkpoint: not supplied"),
    item?.resultId ? h("p", null, `Result identity: ${item.resultId}`) : null,
    item?.evaluationPolicyId ? h("p", null, `Evaluation policy: ${item.evaluationPolicyId}`) : null,
  );
}
