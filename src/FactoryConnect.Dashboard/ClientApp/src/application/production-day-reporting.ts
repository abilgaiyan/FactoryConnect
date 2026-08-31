import type { ProductionDayQueryRequest } from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

const productionDayPattern = /^\d{4}-\d{2}-\d{2}$/;
const firstQueryableProductionDay = "0001-01-01";
const lastQueryableProductionDay = "9999-12-30";
const firstPageSize = 100;

export function buildProductionDayQueryRequest(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
): ProductionDayQueryRequest {
  return {
    sources: sources.map(({ machineId, processorId }) => ({ machineId, processorId })),
    fromInclusive: productionDay,
    toExclusive: nextProductionDay(productionDay),
    metrics: null,
    context: null,
    statuses: null,
    order: "period-ascending",
    pageSize: firstPageSize,
    continuationToken: null,
  };
}

export function isProductionDaySelection(value: string): boolean {
  if (
    !productionDayPattern.test(value) ||
    value < firstQueryableProductionDay ||
    value > lastQueryableProductionDay
  ) {
    return false;
  }

  const date = new Date(`${value}T00:00:00.000Z`);
  return !Number.isNaN(date.valueOf()) && date.toISOString().slice(0, 10) === value;
}

function nextProductionDay(productionDay: string): string {
  if (!isProductionDaySelection(productionDay)) {
    throw new RangeError(
      "Production day must be a valid queryable YYYY-MM-DD calendar date from 0001-01-01 through 9999-12-30.",
    );
  }

  const date = new Date(`${productionDay}T00:00:00.000Z`);
  date.setUTCDate(date.getUTCDate() + 1);
  return date.toISOString().slice(0, 10);
}
