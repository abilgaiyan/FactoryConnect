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

const supersededCancellationReason = Symbol("query-superseded");
const disposedCancellationReason = Symbol("query-controller-disposed");

type ApplicationCancellationReason =
  | typeof supersededCancellationReason
  | typeof disposedCancellationReason;

interface ActiveExecution {
  readonly generation: number;
  readonly controller: AbortController;
  applicationCancellationReason?: ApplicationCancellationReason;
}

export interface QueryLifecycleController<T> {
  current(): QueryState<T>;
  subscribe(listener: (state: QueryState<T>) => void): () => void;
  execute(): Promise<QueryState<T>>;
  dispose(): void;
}

export interface QueryLifecycleControllerOptions<T> {
  readonly query: (signal: AbortSignal) => Promise<T>;
  readonly isEmpty: (data: T) => boolean;
}

export function createQueryLifecycleController<T>(
  options: QueryLifecycleControllerOptions<T>,
): QueryLifecycleController<T> {
  let state: QueryState<T> = { kind: "idle" };
  let nextGeneration = 0;
  let activeExecution: ActiveExecution | undefined;
  let disposed = false;
  const listeners = new Set<(state: QueryState<T>) => void>();

  const publish = (next: QueryState<T>): QueryState<T> => {
    state = next;
    for (const listener of listeners) {
      listener(state);
    }

    return state;
  };

  const ownsPublication = (execution: ActiveExecution): boolean =>
    !disposed && activeExecution?.generation === execution.generation;

  const cancel = (
    execution: ActiveExecution,
    reason: ApplicationCancellationReason,
  ): void => {
    execution.applicationCancellationReason = reason;
    execution.controller.abort(reason);
  };

  return {
    current() {
      return state;
    },

    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },

    async execute() {
      if (disposed) {
        throw new Error("Query lifecycle controller is disposed.");
      }

      if (activeExecution !== undefined) {
        cancel(activeExecution, supersededCancellationReason);
      }

      const execution: ActiveExecution = {
        generation: ++nextGeneration,
        controller: new AbortController(),
      };
      activeExecution = execution;
      publish({ kind: "loading" });

      try {
        const data = await options.query(execution.controller.signal);
        if (!ownsPublication(execution)) {
          return state;
        }

        activeExecution = undefined;
        return publish(options.isEmpty(data) ? { kind: "empty" } : { kind: "success", data });
      } catch (error) {
        if (!ownsPublication(execution)) {
          if (!isReportingClientFailure(error)) {
            throw error;
          }

          return state;
        }

        activeExecution = undefined;

        if (error instanceof ReportingInvalidQueryFailure) {
          return publish({ kind: "invalidRequest", details: error.problemDetails });
        }

        if (isReportingClientFailure(error)) {
          return publish({ kind: "failed", failure: error });
        }

        publish({ kind: "idle" });
        throw error;
      }
    },

    dispose() {
      if (disposed) {
        return;
      }

      disposed = true;
      if (activeExecution !== undefined) {
        cancel(activeExecution, disposedCancellationReason);
        activeExecution = undefined;
      }

      listeners.clear();
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
