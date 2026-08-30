import type { paths } from "../generated/reporting-contract";

type ShiftQueryOperation = NonNullable<
  paths["/api/reporting/v1/operational-metrics/shifts/query"]["post"]
>;

type ProductionDayQueryOperation = NonNullable<
  paths["/api/reporting/v1/operational-metrics/production-days/query"]["post"]
>;

export type ShiftQueryRequest =
  ShiftQueryOperation["requestBody"]["content"]["application/json"];

export type ProductionDayQueryRequest =
  ProductionDayQueryOperation["requestBody"]["content"]["application/json"];

export type OperationalMetricPage =
  ShiftQueryOperation["responses"][200]["content"]["application/json"];

export type ReportingProblemDetails =
  ShiftQueryOperation["responses"][400]["content"]["application/problem+json"];

export interface ReportingRequestOptions {
  signal?: AbortSignal;
}

export interface ReportingClientOptions {
  baseAddress: string;
  timeoutMilliseconds: number;
  fetch?: typeof globalThis.fetch;
}

export interface ReportingClient {
  queryShiftMetrics(
    request: ShiftQueryRequest,
    options?: ReportingRequestOptions,
  ): Promise<OperationalMetricPage>;

  queryProductionDayMetrics(
    request: ProductionDayQueryRequest,
    options?: ReportingRequestOptions,
  ): Promise<OperationalMetricPage>;
}

export type ProductionDayOperationalMetricPage =
  ProductionDayQueryOperation["responses"][200]["content"]["application/json"];

export type ProductionDayReportingProblemDetails =
  ProductionDayQueryOperation["responses"][400]["content"]["application/problem+json"];
