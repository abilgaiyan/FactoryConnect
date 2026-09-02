import type { PresentedMetric } from "./shift-performance-model.ts";

const calculated: PresentedMetric = {
  state: "calculated",
  metricKey: "OEE",
  version: "1.0",
  value: "0.37000000000000000000",
  unit: "Ratio",
};

const unavailable: PresentedMetric = {
  state: "unavailable",
  metricKey: "Availability",
  version: "1.0",
  reasonCode: null,
  reasonOperandName: null,
};

const insufficientEvidence: PresentedMetric = {
  state: "insufficient-evidence",
  metricKey: "Quality",
  version: "1.0",
  reasonCode: "missing-input",
  reasonOperandName: "GoodCount",
};

const missing: PresentedMetric = {
  state: "missing",
  metricKey: "Performance",
  version: "1.0",
};

void calculated;
void unavailable;
void insufficientEvidence;
void missing;

// @ts-expect-error calculated metrics require a non-null authoritative value.
const calculatedWithNullValue: PresentedMetric = {
  state: "calculated",
  metricKey: "OEE",
  version: "1.0",
  value: null,
  unit: "Ratio",
};

// @ts-expect-error calculated metrics require a non-null unit.
const calculatedWithNullUnit: PresentedMetric = {
  state: "calculated",
  metricKey: "OEE",
  version: "1.0",
  value: 0.37,
  unit: null,
};

// @ts-expect-error unavailable metrics cannot carry a calculated value.
const unavailableWithValue: PresentedMetric = {
  state: "unavailable",
  metricKey: "Availability",
  version: "1.0",
  value: 0.25,
  reasonCode: null,
  reasonOperandName: null,
};

// @ts-expect-error missing metrics cannot manufacture value, unit, or reason fields.
const missingWithAuthoritativeFields: PresentedMetric = {
  state: "missing",
  metricKey: "Quality",
  version: "1.0",
  value: 0,
  unit: "Ratio",
  reasonCode: "invented",
  reasonOperandName: null,
};

const unknownMetricKey: PresentedMetric = {
  state: "missing",
  // @ts-expect-error only the five exact shift-overview metric keys are valid.
  metricKey: "Efficiency",
  version: "1.0",
};

const wrongVersion: PresentedMetric = {
  state: "missing",
  metricKey: "OEE",
  // @ts-expect-error FC-029.3B preserves the exact 1.0 definition identity.
  version: "2.0",
};

void calculatedWithNullValue;
void calculatedWithNullUnit;
void unavailableWithValue;
void missingWithAuthoritativeFields;
void unknownMetricKey;
void wrongVersion;
