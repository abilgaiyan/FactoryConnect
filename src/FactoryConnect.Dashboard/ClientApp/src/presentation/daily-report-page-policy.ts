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
  shiftId = "",
): DailyReportModel {
  if (productionLineId === "" && machineId === "" && shiftId === "") {
    return model;
  }

  return {
    ...model,
    groups: model.groups
      .map(group => ({
        ...group,
        machines: group.machines.filter(machine =>
          (productionLineId === "" || machine.productionLineId === productionLineId) &&
          (machineId === "" || machine.machineId === machineId) &&
          (shiftId === "" || machine.shifts.some(shift => shift.shift.shiftId === shiftId)))
          .map(machine => shiftId === "" ? machine : {
            ...machine,
            shifts: machine.shifts.filter(shift => shift.shift.shiftId === shiftId),
          }),
      }))
      .filter(group => group.machines.length > 0),
  };
}

/**
 * Count authoritative OEE cell states for the currently selected scope.
 * Counts are coverage indicators, not a factory-wide or averaged OEE percentage.
 */
export function summarizeDailyReportOee(model: DailyReportModel, shiftFiltered: boolean) {
  const summary = { machines: 0, calculated: 0, unavailable: 0, insufficientEvidence: 0, missing: 0 };
  for (const group of model.groups) {
    for (const machine of group.machines) {
      summary.machines++;
      const scopes = shiftFiltered ? machine.shifts.map(shift => shift.cells) : [machine.productionDayCells];
      for (const cells of scopes) {
        const oee = cells.find(cell => cell.metricKey === "oee");
        switch (oee?.state) {
          case "calculated": summary.calculated++; break;
          case "unavailable": summary.unavailable++; break;
          case "insufficient-evidence": summary.insufficientEvidence++; break;
          default: summary.missing++; break;
        }
      }
    }
  }
  return summary;
}
