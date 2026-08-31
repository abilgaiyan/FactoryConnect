import { useCallback, useEffect, useMemo } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import { buildProductionDayQueryRequest } from "./production-day-reporting.ts";
import { createQueryLifecycleController } from "../query/query-lifecycle-controller.ts";
import type { QueryLifecycleBinding } from "../query/use-query-lifecycle-controller.ts";
import { useQueryLifecycleController } from "../query/use-query-lifecycle-controller.ts";
import type { OperationalMetricPage } from "../api/reporting/index.ts";

export function useProductionDayReporting(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): QueryLifecycleBinding<OperationalMetricPage> {
  const request = useMemo(
    () => buildProductionDayQueryRequest(productionDay, runtime.configuration.sources),
    [productionDay, runtime.configuration.sources],
  );
  const createController = useCallback(
    () => createQueryLifecycleController({
      query: (signal) => runtime.reportingClient.queryProductionDayMetrics(request, { signal }),
      isEmpty: (page) => page.items.length === 0,
    }),
    [request, runtime.reportingClient],
  );
  const query = useQueryLifecycleController(createController);

  useEffect(() => {
    void query.execute();
  }, [createController, query.execute]);

  return query;
}
