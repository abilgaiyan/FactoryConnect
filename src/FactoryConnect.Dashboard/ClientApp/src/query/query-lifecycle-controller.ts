import {
  ReportingCancellationFailure,
  ReportingHttpFailure,
  ReportingIncompatibleContinuationTokenFailure,
  ReportingInvalidQueryFailure,
  ReportingMalformedContinuationTokenFailure,
  ReportingNetworkFailure,
  ReportingProtocolFailure,
  ReportingTimeoutFailure,
  type ReportingClientFailure,
} from "../api/reporting/index.ts";
import type { QueryState } from "./query-state.ts";

export interface QueryLifecycleController<T> {
  current(): QueryState<T>;
  subscribe(listener: (state: QueryState<T>) => void): () => void;
  execute(): Promise<QueryState<T>>;
}

export interface QueryLifecycleControllerOptions<T> {
  readonly query: () => Promise<T>;
  readonly isEmpty: (data: T) => boolean;
}

export function createQueryLifecycleController<T>(
  options: QueryLifecycleControllerOptions<T>,
): QueryLifecycleController<T> {
  let state: QueryState<T> = { kind: "idle" };
  const listeners = new Set<(state: QueryState<T>) => void>();

  const publish = (next: QueryState<T>): QueryState<T> => {
    state = next;
    for (const listener of listeners) {
      listener(state);
    }

    return state;
  };

  return {
    current() {
      return state;
    },

    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    async execute() {
      publish({ kind: "loading" });

      try {
        const data = await options.query();
        return publish(options.isEmpty(data) ? { kind: "empty" } : { kind: "success", data });
      } catch (error) {
        if (error instanceof ReportingInvalidQueryFailure) {
          return publish({ kind: "invalidRequest", details: error.problemDetails });
        }

        if (isReportingClientFailure(error)) {
          return publish({ kind: "failed", failure: error });
        }

        throw error;
      }
    },
  };
}

function isReportingClientFailure(error: unknown): error is ReportingClientFailure {
  return (
    error instanceof ReportingCancellationFailure ||
    error instanceof ReportingTimeoutFailure ||
    error instanceof ReportingNetworkFailure ||
    error instanceof ReportingMalformedContinuationTokenFailure ||
    error instanceof ReportingIncompatibleContinuationTokenFailure ||
    error instanceof ReportingHttpFailure ||
    error instanceof ReportingProtocolFailure
  );
}
