import type { CalculatedMetricDisplay, MetricValue } from "./production-day-presentation.ts";

const ratioUnit = "ratio";

export function formatCalculatedMetric(metric: CalculatedMetricDisplay): string {
  if (metric.unit.toLowerCase() === ratioUnit) {
    return `${formatRatioAsPercentage(metric.value)}%`;
  }

  return `${String(metric.value)} ${metric.unit}`;
}

export function formatRatioAsPercentage(value: MetricValue): string {
  const text = typeof value === "number" ? String(value) : value;
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(text);
  if (match === null) {
    return `${text} × 100`;
  }

  const sign = match[1] ?? "";
  const integer = match[2] ?? "0";
  const fraction = match[3] ?? "";
  const digits = `${integer}${fraction}`.replace(/^0+(?=\d)/, "");
  const decimalPlaces = Math.max(fraction.length - 2, 0);
  const padded = digits.padStart(decimalPlaces + 1, "0");

  if (decimalPlaces === 0) {
    return `${sign}${padded}`;
  }

  const split = padded.length - decimalPlaces;
  const whole = padded.slice(0, split);
  const decimal = padded.slice(split).replace(/0+$/, "");
  return decimal.length === 0 ? `${sign}${whole}` : `${sign}${whole}.${decimal}`;
}
