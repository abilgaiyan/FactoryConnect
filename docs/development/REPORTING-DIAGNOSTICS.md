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
