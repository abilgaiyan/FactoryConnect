export const reportingRoutes = {
  shiftQuery: "api/reporting/v1/operational-metrics/shifts/query",
  productionDayQuery: "api/reporting/v1/operational-metrics/production-days/query",
} as const;

export type ReportingRoute =
  (typeof reportingRoutes)[keyof typeof reportingRoutes];
