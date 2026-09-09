import assert from "node:assert/strict";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { dailyReportPath } from "../src/application/daily-report-navigation.ts";
import {
  DailyReportPage,
  canPrintDailyReportState,
  visibleDailyReportModel,
} from "../src/presentation/DailyReportPage.tsx";

const day = "2026-09-09";

function cell(metricKey, state, overrides = {}) {
  if (state === "calculated") {
    return { metricKey, version: "1.0", state, value: "0.75", unit: "Ratio", ...overrides };
  }
  if (state === "missing") {
    return { metricKey, version: "1.0", state, ...overrides };
  }
  return {
    metricKey,
    version: "1.0",
    state,
    reasonCode: "missing-input",
    reasonOperandName: "ActualProductionTime",
    ...overrides,
  };
}

function cells() {
  return [
    cell("Availability", "calculated", { value: 0 }),
    cell("Utilization", "missing"),
    cell("Performance", "unavailable"),
    cell("Quality", "insufficient-evidence"),
    cell("OEE", "calculated", { value: "0.31" }),
  ];
}

function model() {
  return {
    productionDay: day,
    groups: [{
      groupName: "Line A",
      machines: [{
        machineId: "M1",
        processorId: "P1",
        siteId: "site-a",
        productionLineId: "line-a",
        displayName: "Machine 1",
        groupName: "Line A",
        displayOrder: 10,
        productionDayCells: cells(),
        shifts: [{
          shift: {
            siteId: "site-a",
            shiftScheduleAssignmentId: "assignment-a",
            shiftId: "Shift A",
            startsAtUtc: "2026-09-09T00:00:00Z",
            endsAtUtc: "2026-09-09T08:00:00Z",
          },
          productionLineId: "line-a",
          sourceRevision: null,
          cells: cells(),
        }],
      }],
    }],
  };
}

function render(state) {
  return renderToStaticMarkup(React.createElement(DailyReportPage, {
    productionDay: day,
    state,
    async refresh() {},
    print() {},
  }));
}

function childrenOf(element) {
  const children = element?.props?.children;
  if (children === undefined || children === null) return [];
  return Array.isArray(children) ? children.flat(Infinity).filter(Boolean) : [children];
}

function findElements(element, predicate, found = []) {
  if (React.isValidElement(element) && predicate(element)) found.push(element);
  for (const child of childrenOf(element)) {
    if (React.isValidElement(child)) findElements(child, predicate, found);
  }
  return found;
}

test("canonical Daily Report navigation path remains production-day scoped", () => {
  assert.equal(dailyReportPath(day), "/production-days/2026-09-09/report");
});

test("successful report preserves machine, roster shift, fixed metric membership, and textual cell states", () => {
  const html = render({ kind: "success", productionDay: day, model: model(), canPrint: true });

  assert.match(html, /FactoryConnect/);
  assert.match(html, /Daily Report/);
  assert.match(html, /2026-09-09/);
  assert.match(html, /Line A/);
  assert.match(html, /Machine 1/);
  assert.match(html, /Shift A/);
  for (const metric of ["Availability", "Utilization", "Performance", "Quality", "OEE"]) {
    assert.ok(html.includes(`>${metric}</th>`));
  }
  assert.match(html, /Calculated/);
  assert.match(html, /Unavailable/);
  assert.match(html, /Insufficient evidence/);
  assert.match(html, /Missing/);
  assert.match(html, /Reason: missing-input · ActualProductionTime/);
  assert.match(html, />0 Ratio</);
  assert.match(html, />0.31 Ratio</);
});

test("print is authorized only for the current successful generation", () => {
  const currentModel = model();
  const success = { kind: "success", productionDay: day, model: currentModel, canPrint: true };
  const refreshing = { kind: "refreshing", productionDay: day, previous: currentModel, canPrint: false };
  const failed = {
    kind: "presentation-contract-failure",
    productionDay: day,
    failure: new Error("contract"),
    previous: currentModel,
    canPrint: false,
  };

  assert.equal(canPrintDailyReportState(success), true);
  assert.equal(canPrintDailyReportState(refreshing), false);
  assert.equal(canPrintDailyReportState(failed), false);
  assert.equal(visibleDailyReportModel(refreshing), currentModel);
  assert.equal(visibleDailyReportModel(failed), currentModel);

  let printCalls = 0;
  const element = DailyReportPage({
    productionDay: day,
    state: success,
    async refresh() {},
    print() { printCalls += 1; },
  });
  const buttons = findElements(element, candidate => candidate.type === "button");
  assert.equal(buttons.length, 2);
  const printButton = buttons[1];
  assert.equal(printButton.props.disabled, false);
  printButton.props.onClick();
  assert.equal(printCalls, 1);
});

test("stale same-day model remains visible during refresh and presentation failure but is explicitly non-printable", () => {
  const previous = model();

  const refreshingHtml = render({
    kind: "refreshing",
    productionDay: day,
    previous,
    canPrint: false,
  });
  assert.match(refreshingHtml, /previous successful retrieval and cannot be printed/);
  assert.match(refreshingHtml, /Machine 1/);
  assert.match(refreshingHtml, /Print<\/button>/);
  assert.match(refreshingHtml, /disabled=""[^>]*>Print<\/button>/);

  const failedHtml = render({
    kind: "presentation-contract-failure",
    productionDay: day,
    failure: new Error("contract"),
    previous,
    canPrint: false,
  });
  assert.match(failedHtml, /presentation contract/);
  assert.match(failedHtml, /stale and cannot be printed/);
  assert.match(failedHtml, /Machine 1/);
  assert.equal(failedHtml.includes("DailyReportModel"), false);
});
