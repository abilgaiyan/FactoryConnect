import {
  createReportingClient,
  type ReportingClient,
} from "../api/reporting/index.ts";
import {
  loadDashboardRuntimeConfiguration,
  type DashboardRuntimeConfiguration,
} from "./runtime-configuration.ts";

export interface DashboardApplicationRuntime {
  readonly configuration: DashboardRuntimeConfiguration;
  readonly reportingClient: ReportingClient;
}

export async function createDashboardApplicationRuntime(
  origin: string,
  fetchImplementation: typeof globalThis.fetch = globalThis.fetch,
): Promise<DashboardApplicationRuntime> {
  const configuration = await loadDashboardRuntimeConfiguration(fetchImplementation);
  const baseAddress = new URL(configuration.reportingBasePath, origin).toString();
  const reportingClient = createReportingClient({
    baseAddress,
    timeoutMilliseconds: configuration.requestTimeoutMilliseconds,
    fetch: fetchImplementation,
  });

  return {
    configuration,
    reportingClient,
  };
}
