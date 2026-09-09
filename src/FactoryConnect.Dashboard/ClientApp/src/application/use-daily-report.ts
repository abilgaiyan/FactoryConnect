import { useEffect, useMemo, useState } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  createDailyReportLifecycleController,
  type DailyReportLifecycleState,
} from "./daily-report-lifecycle.ts";

export interface DailyReportBinding {
  readonly state: DailyReportLifecycleState;
  readonly refresh: () => Promise<void>;
}

export function useDailyReport(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): DailyReportBinding {
  const controller = useMemo(
    () => createDailyReportLifecycleController({ reportingClient: runtime.reportingClient }),
    [runtime.reportingClient],
  );
  const [state, setState] = useState<DailyReportLifecycleState>(() => controller.current());

  useEffect(() => {
    const unsubscribe = controller.subscribe(setState);
    void controller.execute(productionDay, runtime.configuration.sources);

    return () => {
      unsubscribe();
      controller.dispose();
    };
  }, [controller, productionDay, runtime.configuration.sources]);

  const refresh = async () => {
    await controller.execute(productionDay, runtime.configuration.sources);
  };

  return { state, refresh };
}
