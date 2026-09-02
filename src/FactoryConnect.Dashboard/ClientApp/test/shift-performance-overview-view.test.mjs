import assert from "node:assert/strict";
import { after, test } from "node:test";

import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createServer } from "vite";

const vite = await createServer({
  appType: "custom",
  logLevel: "silent",
  server: { middlewareMode: true },
});
const { ShiftPerformanceOverviewView } = await vite.ssrLoadModule(
  "/src/presentation/ShiftPerformanceOverviewView.tsx",
);

after(async () => {
  await vite.close();
});

const day = "2026-09-02";

function render(overview) {
  return renderToStaticMarkup(createElement(ShiftPerformanceOverviewView, { overview }));
}

function metric(metricKey, state, overrides = {}) {
  if (state === "calculated") {
    return { metricKey, version: "1.0", state, value: "0.80", unit: "Ratio", ...overrides };
  }
  if (state === "missing") {
    return { metricKey, version: "1.0", state };
  }
  return {
    metricKey,
    version: "1.0",
    state,
    reasonCode: "missing-input",
    reasonOperandName: "Input",
    ...overrides,
  };
}

function shift(overrides = {}) {
  return {
    shift: {
      siteId: "site-a",
      shiftScheduleAssignmentId: "assignment-a",
      shiftId: "SHIFT-BETA",
      startsAtUtc: "2026-09-02T08:00:00.0000000Z",
      endsAtUtc: "2026-09-02T16:00:00+00:00",
    },
    productionLineId: "line-a",
    sourceRevision: null,
    availability: metric("Availability", "missing"),
    utilization: metric("Utilization", "missing"),
    performance: metric("Performance", "missing"),
    quality: metric("Quality", "missing"),
    oee: metric("OEE", "missing"),
    ...overrides,
  };
}

function machine(machineId, displayName, shifts = []) {
  return {
    machineId,
    processorId: `processor-${machineId}`,
    siteId: "site-a",
    productionLineId: "line-a",
    displayName,
    shifts,
  };
}

function overview(groups) {
  return { productionDay: day, groups };
}

test("renders the empty configured factory distinctly", () => {
  const html = render(overview([]));
  assert.match(html, /No configured machines\./);
  assert.doesNotMatch(html, /No authoritative shift occurrences returned\./);
});

test("renders a configured machine with no authoritative occurrences distinctly", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One")] }]));
  assert.match(html, /Machine One/);
  assert.match(html, /No authoritative shift occurrences returned\./);
  assert.doesNotMatch(html, /No configured machines\./);
});

test("renders a zero-evidence occurrence with five accessible missing metric cells", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [shift()])] }]));
  assert.match(html, /SHIFT-BETA/);
  for (const metricKey of ["Availability", "Utilization", "Performance", "Quality", "OEE"]) {
    assert.match(html, new RegExp(`aria-label="${metricKey} missing"`));
  }
  assert.equal((html.match(/>—<\/td>/g) ?? []).length, 5);
});

test("preserves configured group and machine order", () => {
  const html = render(overview([
    { groupName: "Line B", machines: [machine("M2", "Machine Two"), machine("M4", "Machine Four")] },
    { groupName: "Line A", machines: [machine("M1", "Machine One"), machine("M3", "Machine Three")] },
  ]));

  assert.ok(html.indexOf("Line B") < html.indexOf("Line A"));
  assert.ok(html.indexOf("Machine Two") < html.indexOf("Machine Four"));
  assert.ok(html.indexOf("Machine One") < html.indexOf("Machine Three"));
});

test("displays authoritative shift identity and UTC wire timestamps verbatim", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [shift()])] }]));
  assert.match(html, /SHIFT-BETA/);
  assert.match(html, /2026-09-02T08:00:00\.0000000Z/);
  assert.match(html, /2026-09-02T16:00:00\+00:00/);
  assert.doesNotMatch(html, /Shift 1/);
});

test("renders precision-preserving calculated ratios and authoritative inconsistent OEE", () => {
  const renderedShift = shift({
    availability: metric("Availability", "calculated", { value: "0.8000" }),
    performance: metric("Performance", "calculated", { value: "0.5000" }),
    quality: metric("Quality", "calculated", { value: "0.9000" }),
    oee: metric("OEE", "calculated", { value: "0.3700" }),
  });
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [renderedShift])] }]));

  assert.match(html, />80%<\/td>/);
  assert.match(html, />50%<\/td>/);
  assert.match(html, />90%<\/td>/);
  assert.match(html, />37%<\/td>/);
  assert.doesNotMatch(html, />36%<\/td>/);
});

test("renders unavailable and insufficient-evidence as distinct visible states with reason evidence", () => {
  const renderedShift = shift({
    utilization: metric("Utilization", "unavailable", {
      reasonCode: "missing-power-on",
      reasonOperandName: "PowerOn",
    }),
    performance: metric("Performance", "insufficient-evidence", {
      reasonCode: "missing-reference-time",
      reasonOperandName: "ReferenceTime",
    }),
  });
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [renderedShift])] }]));

  assert.match(html, /<strong>Unavailable<\/strong>/);
  assert.match(html, /missing-power-on \/ PowerOn/);
  assert.match(html, /<strong>Insufficient evidence<\/strong>/);
  assert.match(html, /missing-reference-time \/ ReferenceTime/);
  assert.doesNotMatch(html, /title="missing-power-on/);
});

test("does not manufacture current machine state vocabulary", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [shift()])] }]));
  for (const state of ["Running", "Idle", "Fault", "Offline", "Shutdown"]) {
    assert.doesNotMatch(html, new RegExp(`\\b${state}\\b`, "i"));
  }
});
