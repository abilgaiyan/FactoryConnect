import type { ProductionDayShiftQueryRequest } from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

const productionDayPattern = /^\d{4}-\d{2}-\d{2}$/;
const firstProductionDay = "0001-01-01";
const lastProductionDay = "9999-12-31";
const pageSize = 200;

const shiftOverviewMetrics = [
  { metricKey: "Availability", version: "1.0" },
  { metricKey: "Utilization", version: "1.0" },
  { metricKey: "Performance", version: "1.0" },
  { metricKey: "Quality", version: "1.0" },
  { metricKey: "OEE", version: "1.0" },
] as const;

type UnpartitionedContextRequest = NonNullable<ProductionDayShiftQueryRequest["context"]> & {
  unpartitionedOnly: boolean;
};

export function buildProductionDayShiftQueryRequest(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  continuationToken: string | null = null,
): ProductionDayShiftQueryRequest {
  if (!isProductionDayIdentity(productionDay)) {
    throw new RangeError(
      "Production day must be a valid YYYY-MM-DD calendar identity from 0001-01-01 through 9999-12-31.",
    );
  }

  const context: UnpartitionedContextRequest = {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    unpartitionedOnly: true,
  };

  return {
    sources: sources.map(({ machineId, processorId, siteId }) => ({
      machineId,
      processorId,
      siteId,
      businessDate: productionDay,
    })),
    context,
    metrics: shiftOverviewMetrics.map(({ metricKey, version }) => ({ metricKey, version })),
    statuses: null,
    pageSize,
    continuationToken,
  };
}

export function isProductionDayIdentity(value: string): boolean {
  if (!productionDayPattern.test(value)
    || value < firstProductionDay
    || value > lastProductionDay) {
    return false;
  }

  const [yearText, monthText, dayText] = value.split("-");
  const year = Number(yearText);
  const month = Number(monthText);
  const day = Number(dayText);
  const daysInMonth = monthLength(year, month);

  return daysInMonth !== null && day >= 1 && day <= daysInMonth;
}

function monthLength(year: number, month: number): number | null {
  switch (month) {
    case 1:
    case 3:
    case 5:
    case 7:
    case 8:
    case 10:
    case 12:
      return 31;
    case 4:
    case 6:
    case 9:
    case 11:
      return 30;
    case 2:
      return isLeapYear(year) ? 29 : 28;
    default:
      return null;
  }
}

function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}
