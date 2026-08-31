import type {
  ReportingClientFailure,
  ReportingProblemDetails,
} from "../api/reporting/index.ts";

export type QueryState<T> =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "success"; data: T }
  | { kind: "empty" }
  | { kind: "invalidRequest"; details: ReportingProblemDetails }
  | { kind: "failed"; failure: ReportingClientFailure };
