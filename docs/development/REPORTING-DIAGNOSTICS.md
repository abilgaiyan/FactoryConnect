# Retained-publication reporting diagnostics

Baseline: 088bf52492546a2a79029cf366a22f7a4d7e8d44. Diagnostics only: no SQL,
verification, timeout or cache-policy optimization is intended.

Enable `ReportingDiagnostics:Enabled=true` on the local API using the existing
launcher configuration mechanism. Disable to roll back diagnostic capture;
revert this PR to remove instrumentation. No migration or database rollback.
Do not change Dashboard's 30-second timeout.

Event 5 emits bounded diagnostic metadata with trace/span/parent IDs. Outcomes
are Completed, Cancelled or Failed. The publication operation includes the
initial gate, whereas the existing publication.verify span starts after it.
Per-row completion follows every retained/archive/reconstruction check.
A shared checkpoint is not equivalent result evaluation. Evidence-cache
lookups/hits/misses/loads refer to one shared validated input+aggregate entry.

SQL execution ends at ExecuteReaderAsync/ExecuteScalarAsync completion.
Consumption measures the reader lifetime before disposal, including ReadAsync,
field materialization and intervening validation. It is not pure network time.
Cleanup measures DisposeAsync separately. Row counts include zero-row readers.
Scalar commands do not claim a result-set row count. Numeric site suffixes are
source-order command-site IDs, not SQL hashes or parameter values. Generic
retained queries carry explicit inventory/payload/evidence identifiers.

The new scopes are capped at 4096 per root Activity. A truncated capture is
incomplete and cannot prove total command counts. Existing diagnostic spans
are unchanged and are not governed by the new scope budget. Instrumentation
cost must be measured with enabled/disabled pairs.

`ReportingDiagnostics:IncludeCanonicalIdentities=true` is a SECOND explicit
opt-in. Event 6 then contains canonical identity bytes (hex), including context
identifiers; restrict access to these logs. It contains no archive/evidence
payloads or SQL parameters. Normal logs contain a versioned SHA-256 fingerprint.
Compare canonical bytes, not hashes alone, to establish identity equality.
The envelope includes canonical evaluation key, projection processor,
aggregation processor, stream machine/key, UInt64 cut, policy and format.

## Validation before use

This draft requires .NET 10 compilation/analyzer checks and SQL integration
acceptance. Scope outcome/export-failure tests are included. Full command-site
coverage, enabled/disabled SQL sequence and result equivalence, corruption and
cancellation cases, fingerprint vectors, budget behavior and overhead remain
acceptance gates. In particular shared helper calls outside instrumented files
must be audited before claiming all SQL time is captured. Transaction attempts
have their own spans; retry decisions and SQL ordering are unchanged.

Use the unchanged restored database and exact October 28 GG-003/MOXA requests.
Capture one fresh-process request (not claimed to be cold SQL), then five warm
runs, sequentially, separately for direct API and Dashboard. Preserve request,
response, code SHA and database identity securely. Cancelled Dashboard traces
are partial work. Do not add nested durations or call residual elapsed CPU.
Record logical reads/waits separately through approved SQL Server capture.
Do not clear caches, restart SQL or mutate application data.

## v0.3 summary amendment

With ReportingDiagnostics enabled, the API owns a ReportingDiagnosticSession.
Validated source selections reserve details for up to eight distinct processors
in their existing source order. Each receives 256 start-attempt admissions;
2048 additional admissions are shared, including non-processor details. Extra
processors are summary-only. Unknown populations reserve the first eight seen.
Retries share request-level capacity but have independent attempt IDs/counters.
No reservation is borrowed; shared admissions follow actual call order. Sampling
and emission failure do not refund an admission. Existing direct ActivitySource
spans are outside this ceiling: 4096 is NOT a limit on total log records.

Event 7, category FactoryConnect.Reporting.Summary, emits one terminal record
per stable-read attempt, independently of detail admission and Activity sampling.
Event 8, category FactoryConnect.Reporting.SummaryFailure, is the separate bounded
fallback. These are independently configurable channels but may share underlying
logging providers; neither promises durable storage. Event 9 acknowledges sink
acceptance separately from summary content. If all channels fail, delivery cannot
be established. Logger disablement is not treated as successful sink acceptance.
No diagnostic failure replaces the reporting result/exception.

Started/Completed/Failed/Cancelled/Active counts are collected for rows, root
reconstructions, SQL execution, consumption, cleanup, gates and verification.
RowsInterrupted = Failed + Cancelled + Active. EnumerationCompleted is set only
after the retained-row loop (or a verified empty publication). SQL coverage stays
Unknown until positive command-site coverage is independently demonstrated.
Elapsed category totals overlap; they are not exclusive wall-clock phases.
Operations still active at finalization produce Degraded integrity; collection
faults produce Unknown. Known counters remain. Active operation identities are
bounded to 64; truncation is explicit. Distinct checkpoints are bounded to 4096;
overflow marks the cardinality truncated and summary integrity Unknown.
Reference-time validation/load and cache-hit counters update as operations occur.
Pre-attempt roster/connection activity is not included in processor-attempt SQL
totals. Summary counters cover instrumented command sites, not all SQL implicitly.

Disabling ReportingDiagnostics disables session creation. No SQL, isolation,
verification membership or reporting timeout changes are part of the amendment.
Added database-free tests cover budget exhaustion/fairness, known/unknown ninth
processor, concurrent admission, retry isolation, active outcomes, cancellation,
collector failure, primary/fallback failure and no-session behavior. Compilation,
.NET/SQL integration checks and overhead/equivalence captures remain required.
