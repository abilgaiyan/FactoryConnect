import type { CalculatedMetricDisplay, MetricValue } from "./production-day-presentation.ts";

const ratioUnit = "ratio";
const decimalPattern = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/;

export function formatCalculatedMetric(metric: CalculatedMetricDisplay): string {
  if (metric.unit.toLowerCase() === ratioUnit) {
    return `${formatRatioAsPercentage(metric.value)}%`;
  }

  return `${String(metric.value)} ${metric.unit}`;
}

export function formatRatioAsPercentage(value: MetricValue): string {
  const text = typeof value === "number" ? String(value) : value;
  const match = decimalPattern.exec(text);
  if (match === null) {
    return `${text} × 100`;
  }

  const sign = match[1] ?? "";
  const integer = match[2] ?? "0";
  const fraction = match[3] ?? "";
  const exponentText = match[4] ?? "0";
  const exponent = parseExponent(exponentText);
  const digits = `${integer}${fraction}`;
  const decimalIndex = integer.length + exponent + 2;

  let shifted: string;
  if (decimalIndex <= 0) {
    shifted = `0.${"0".repeat(-decimalIndex)}${digits}`;
  } else if (decimalIndex >= digits.length) {
    shifted = `${digits}${"0".repeat(decimalIndex - digits.length)}`;
  } else {
    shifted = `${digits.slice(0, decimalIndex)}.${digits.slice(decimalIndex)}`;
  }

  return `${sign}${normalizeDecimalText(shifted)}`;
}

function parseExponent(value: string): number {
  const negative = value.startsWith("-");
  const digits = value.startsWith("-") || value.startsWith("+") ? value.slice(1) : value;
  let exponent = 0;
  for (const digit of digits) {
    exponent = exponent * 10 + digit.charCodeAt(0) - 48;
  }
  return negative ? -exponent : exponent;
}

function normalizeDecimalText(value: string): string {
  const [wholePart = "0", fractionPart] = value.split(".", 2);
  const whole = wholePart.replace(/^0+(?=\d)/, "");
  if (fractionPart === undefined) {
    return whole;
  }

  const fraction = fractionPart.replace(/0+$/, "");
  return fraction.length === 0 ? whole : `${whole}.${fraction}`;
}
