# Historical contribution load attribution

Diagnostic-only amendment to PR #179. Enable the existing ReportingDiagnostics:Enabled option and Information logging for FactoryConnect.Reporting.Summary. Event 10 emits `Reporting historical load` once per load, independently of Activity sampling/detail-budget exhaustion. No per-row logs are emitted.

Correlate LoadId, TraceId, AttemptId, AggregationProcessor and exact Checkpoint (UInt64 decimal text). SQL text, arbitrary SQL parameter values and historical fact contents are omitted. The explicitly approved processor/checkpoint identities are correlation metadata.

ReadCalls includes the terminal false read and a throwing/interrupted read. ReadCompleted counts calls returning a bool, including false; ReadInterrupted counts calls that throw. RowsReturned counts true reads; RowsAdded counts successful collection insertion only. MaterializationAttempts includes failed iterations. Read and materialize/validate/add totals and maxima include interrupted/failed work through finally blocks. EnumerationCompleted means the reader returned false, not that subsequent aggregation or verification succeeded.

CleanupElapsedMs and CleanupOutcome measure the existing reader disposal separately. NotStarted means the contribution reader was not attached/acquired. Overall Outcome includes failures in other parts of this load, including aggregation and disposal. A successfully collected cancelled-load record does not qualify a report. SqlCoverage remains Unknown.

All timings use Stopwatch.GetTimestamp/GetElapsedTime and are elapsed time, NOT CPU. Read await includes provider/network/server/scheduling work; synchronous getters can also perform provider work. The row-loop is approximately ReadElapsedMs + MaterializeValidateAddElapsedMs + bookkeeping/instrumentation; do not subtract nested SQL/parent spans to infer CPU. LoadElapsedMs also includes identity resolution, aggregation and cleanup. Summary emission occurs after load timing is captured. Timers introduce overhead that remains unmeasured.

SQL literals, parameters, hints, isolation, gates, cancellation token forwarding, materialization, membership checks, collection order and await-using disposal are unchanged. When disabled there is no load collector/session or summary; the same read path executes. Emission failures attempt the existing bounded fallback channel; if both sinks fail delivery is unverified and authoritative exceptions remain unchanged.

Validation required: Release build/analyzers; HistoricalLoadDiagnosticsTests (database-free lifecycle/counter tests); existing SqlContainmentHistoricalReadScopeIntegrationTests, SqlContainmentHistoricalResultIntegrationTests, SqlContainmentVerifiedProjectionHandoffIntegrationTests (real SQL success, cancellation, corruption and ownership); and opt-in/off restored-request equivalence. No SQL or restored-request tests executed during authoring. No performance improvement or full runtime verification claimed.

Rollback: disable the existing diagnostics option, or revert this amendment. No database rollback, migration or timeout change.
