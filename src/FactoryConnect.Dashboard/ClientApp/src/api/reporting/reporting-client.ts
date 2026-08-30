import type {
  OperationalMetricPage,
  ProductionDayQueryRequest,
  ReportingClient,
  ReportingClientOptions,
  ReportingRequestOptions,
  ShiftQueryRequest,
} from "./reporting-client-types.ts";
import { createReportingHttpTransport } from "./reporting-http-transport.ts";
import { createReportingRequestExecutor } from "./reporting-request-executor.ts";
import { createReportingResponseDecoder } from "./reporting-response-decoder.ts";
import { reportingRoutes, type ReportingRoute } from "./reporting-routes.ts";

export function createReportingClient(
  options: ReportingClientOptions,
): ReportingClient {
  const transport = createReportingHttpTransport(options);
  const executor = createReportingRequestExecutor({
    transport,
    timeoutMilliseconds: options.timeoutMilliseconds,
  });
  const decoder = createReportingResponseDecoder();

  const executeAndDecode = async (
    route: ReportingRoute,
    request: unknown,
    requestOptions?: ReportingRequestOptions,
  ): Promise<OperationalMetricPage> => {
    const response = await executor.execute(route, request, requestOptions);
    return decoder.decode(response);
  };

  return {
    queryShiftMetrics(
      request: ShiftQueryRequest,
      requestOptions?: ReportingRequestOptions,
    ) {
      return executeAndDecode(reportingRoutes.shiftQuery, request, requestOptions);
    },

    queryProductionDayMetrics(
      request: ProductionDayQueryRequest,
      requestOptions?: ReportingRequestOptions,
    ) {
      return executeAndDecode(
        reportingRoutes.productionDayQuery,
        request,
        requestOptions,
      );
    },
  };
}
