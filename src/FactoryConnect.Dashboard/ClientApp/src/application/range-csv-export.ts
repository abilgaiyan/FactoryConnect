import type { RangeRow } from "./date-range-reporting.ts";

const headings = [
  "Production day", "Site", "Machine", "Machine ID", "Processor",
  "Metric", "Definition", "Status", "Raw value", "Unit",
  "Reason code", "Reason operand", "Source stream", "Source position",
] as const;

// Quote every field, including spreadsheet-formula-looking identifiers and evidence text.
function csvCell(value: string): string {
  const safe = /^[=+@\-\t\r\n]/.test(value) ? "'" + value : value;
  return '"' + safe.replace(/"/g, '""') + '"';
}

export function exportRangeCsv(rows: readonly RangeRow[]): string {
  const lines = [headings.map(csvCell).join(",")];
  for (const row of rows) {
    for (const item of row.items) {
      lines.push([
        row.day, row.source.siteId, row.source.displayName,
        row.source.machineId, row.source.processorId,
        item.metricKey, item.definitionVersion, item.status,
        item.value === null ? "" : String(item.value), item.unit,
        item.reasonCode ?? "", item.reasonOperandName ?? "",
        item.sourceRevision.streamKey, String(item.sourceRevision.position),
      ].map(csvCell).join(","));
    }
  }
  return lines.join("\r\n") + "\r\n";
}
