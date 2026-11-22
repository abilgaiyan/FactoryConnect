import type { DailyReportCell, DailyReportModel } from "./daily-report-model.ts";

const metricKeys = ["availability", "utilization.elr", "performance", "quality", "oee"] as const;
const columns = ["Production day", "Machine", "Machine ID", "Production line", "Scope", "Shift ID", "Shift start UTC", "Shift end UTC", "Metric", "Status", "Value", "Unit", "Reason code", "Reason operand"];

function csvCell(value: string): string {
  const safe = /^\s*[=+@-]/.test(value) ? "'" + value : value;
  return '"' + safe.replace(/"/g, '""') + '"';
}

/** Export reported evidence only. Missing or unqualified values must not be represented as zero. */
export function dailyReportCsv(model: DailyReportModel, includeProductionDay: boolean): string {
  const rows: string[][] = [columns];
  for (const group of model.groups) {
    for (const machine of group.machines) {
      const scopes = [
        ...(includeProductionDay ? [{ scope: "Production day", shiftId: "", start: "", end: "", cells: machine.productionDayCells }] : []),
        ...machine.shifts.map(shift => ({
          scope: "Shift", shiftId: shift.shift.shiftId, start: shift.shift.startsAtUtc,
          end: shift.shift.endsAtUtc, cells: shift.cells
        }))
      ];
      for (const scope of scopes) {
        for (const key of metricKeys) {
          const cell: DailyReportCell | undefined = scope.cells.find(value => value.metricKey === key);
          rows.push([model.productionDay, machine.displayName, machine.machineId, machine.productionLineId,
            scope.scope, scope.shiftId, scope.start, scope.end, key, cell?.state ?? "missing",
            cell?.state === "calculated" ? String(cell.value) : "",
            cell?.state === "calculated" ? cell.unit : "",
            cell?.state === "unavailable" || cell?.state === "insufficient-evidence" ? cell.reasonCode ?? "" : "",
            cell?.state === "unavailable" || cell?.state === "insufficient-evidence" ? cell.reasonOperandName ?? "" : ""
          ]);
        }
      }
    }
  }
  return "\uFEFF" + rows.map(row => row.map(csvCell).join(",")).join("\r\n");
}
