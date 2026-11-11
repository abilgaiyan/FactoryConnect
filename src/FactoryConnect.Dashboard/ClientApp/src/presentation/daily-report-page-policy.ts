import type { DailyReportLifecycleState } from "../application/daily-report-lifecycle.ts";
import type { DailyReportModel } from "./daily-report-model.ts";

export function canPrintDailyReportState(state: DailyReportLifecycleState): boolean {
  return state.kind === "success" && state.canPrint;
}

export function visibleDailyReportModel(state: DailyReportLifecycleState): DailyReportModel | null {
  switch (state.kind) {
    case "success":
      return state.model;
    case "refreshing":
      return state.previous;
    case "roster-prerequisite-failure":
    case "reporting-failure":
    case "presentation-contract-failure":
    case "invalid-request":
      return state.previous ?? null;
    default:
      return null;
  }
}

/** Select existing authoritative report rows; never recalculate or replace metric evidence. */
export function filterDailyReportModel(
  model: DailyReportModel,
  productionLineId: string,
  machineId: string,
): DailyReportModel {
  if (productionLineId === "" && machineId === "") {
    return model;
  }

  return {
    ...model,
    groups: model.groups
      .map(group => ({
        ...group,
        machines: group.machines.filter(machine =>
          (productionLineId === "" || machine.productionLineId === productionLineId) &&
          (machineId === "" || machine.machineId === machineId)),
      }))
      .filter(group => group.machines.length > 0),
  };
}
