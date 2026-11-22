import assert from "node:assert/strict";
import { register } from "node:module";
import test from "node:test";

register("./tsx-test-loader.mjs", import.meta.url);
const { dailyReportCsv } = await import("../src/presentation/daily-report-csv.ts");

const calculated = (metricKey, value) => ({ metricKey, version: "1.0", state: "calculated", value, unit: "Ratio" });
const unavailable = (metricKey, reasonCode) => ({ metricKey, version: "1.0", state: "unavailable", reasonCode, reasonOperandName: "ReferenceTime" });

const model = {
  productionDay: "2026-11-22",
  groups: [{ groupName: "Line 1", machines: [
    {
      machineId: "Machine-A", displayName: "Gear, Machine", productionLineId: "line-1",
      productionDayCells: [calculated("availability", "0.80"), unavailable("performance", "missing-reference-time")],
      shifts: [{ shift: { shiftId: "Shift 1", startsAtUtc: "2026-11-22T02:00:00Z", endsAtUtc: "2026-11-22T10:00:00Z" },
        cells: [calculated("oee", "0.45")] }]
    },
    {
      machineId: "Machine-B", displayName: "=HYPERLINK(\"bad\")", productionLineId: "line-2",
      productionDayCells: [], shifts: []
    }
  ] }]
};

test("pilot CSV exports both machines with explicit missing OEE and preserved reasons", () => {
  const csv = dailyReportCsv(model, true);
  assert.ok(csv.startsWith("\uFEFF"));
  assert.match(csv, /"Machine-A"/);
  assert.match(csv, /"Machine-B"/);
  assert.match(csv, /"unavailable","","","missing-reference-time","ReferenceTime"/);
  assert.match(csv, /"oee","missing","",""/);
  assert.match(csv, /"oee","calculated","0.45","Ratio"/);
  assert.match(csv, /"'=HYPERLINK\(""bad""\)"/);
  assert.equal(csv.split("\r\n").length, 1 + 15);
});

test("shift-filtered CSV omits production-day rows and does not invent empty-machine metrics", () => {
  const csv = dailyReportCsv(model, false);
  assert.doesNotMatch(csv, /"Production day",""/);
  assert.match(csv, /"Shift","Shift 1"/);
  assert.doesNotMatch(csv, /"Machine-B"/);
  assert.equal(csv.split("\r\n").length, 1 + 5);
});

test("CSV escapes commas, quotes and line breaks in machine names", () => {
  const altered = { ...model, groups: [{ ...model.groups[0], machines: [{ ...model.groups[0].machines[0], displayName: 'A,"B"\nC' }] }] };
  const csv = dailyReportCsv(altered, true);
  assert.match(csv, /"A,""B""\nC"/);
});
