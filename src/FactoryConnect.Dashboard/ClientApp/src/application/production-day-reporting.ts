import type {
  OperationalMetricPage,
  ProductionDayQueryRequest,
  ReportingClient,
  ReportingRequestOptions,
} from "../api/reporting/index.ts";
import type { DashboardRuntimeSource } from "./runtime-configuration.ts";

const productionDayPattern = /^\d{4}-\d{2}-\d{2}$/;
const firstQueryableProductionDay = "0001-01-01";
const lastQueryableProductionDay = "9999-12-30";
const pageSize = 200;

const overviewMetrics = [
  { metricKey: "Availability", version: "1.0" },
  { metricKey: "Utilization", version: "1.0" },
  { metricKey: "Performance", version: "1.0" },
  { metricKey: "Quality", version: "1.0" },
  { metricKey: "OEE", version: "1.0" },
] as const;

type UnpartitionedContextRequest = NonNullable<ProductionDayQueryRequest["context"]> & {
  unpartitionedOnly: boolean;
};

export interface AuthoritativeProductionDayResult {
  readonly items: OperationalMetricPage["items"];
}

export function buildProductionDayQueryRequest(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  continuationToken: string | null = null,
): ProductionDayQueryRequest {
  const context: UnpartitionedContextRequest = {
    productionOrderId: null,
    operationId: null,
    partId: null,
    operatorId: null,
    unpartitionedOnly: true,
  };

  return {
    sources: sources.map(({ machineId, processorId }) => ({ machineId, processorId })),
    fromInclusive: productionDay,
    toExclusive: nextProductionDay(productionDay),
    metrics: overviewMetrics.map(({ metricKey, version }) => ({ metricKey, version })),
    context,
    statuses: null,
    order: "period-ascending",
    pageSize,
    continuationToken,
  };
}

export async function queryAuthoritativeProductionDay(
  productionDay: string,
  sources: readonly DashboardRuntimeSource[],
  reportingClient: Pick<ReportingClient, "queryProductionDayMetrics">,
  options?: ReportingRequestOptions,
): Promise<AuthoritativeProductionDayResult> {
  if (sources.length === 0) {
    return { items: [] };
  }

  const items: OperationalMetricPage["items"] = [];
  let continuationToken: string | null = null;

  do {
    const request = buildProductionDayQueryRequest(
      productionDay,
      sources,
      continuationToken,
    );
    const page = await reportingClient.queryProductionDayMetrics(request, options);
    items.push(...page.items);
    continuationToken = page.continuationToken;
  } while (continuationToken !== null);

  return { items };
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
