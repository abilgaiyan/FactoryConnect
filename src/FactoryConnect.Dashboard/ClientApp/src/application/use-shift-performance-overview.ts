import { useMemo } from "react";

import type { DashboardApplicationRuntime } from "./application-runtime.ts";
import {
  deriveShiftPerformancePageState,
  type ShiftPerformancePageState,
} from "./shift-performance-page-state.ts";
import { useShiftPerformanceReporting } from "./use-shift-performance-reporting.ts";

export interface ShiftPerformanceOverviewBinding {
  readonly state: ShiftPerformancePageState;
  readonly refresh: () => Promise<void>;
}

export function useShiftPerformanceOverview(
  productionDay: string,
  runtime: DashboardApplicationRuntime,
): ShiftPerformanceOverviewBinding {
  const query = useShiftPerformanceReporting(productionDay, runtime);
  const state = useMemo(
    () => deriveShiftPerformancePageState(
      query.state,
      productionDay,
      runtime.configuration.sources,
    ),
    [query.state, productionDay, runtime.configuration.sources],
  );

  return { state, refresh: query.execute };
}
