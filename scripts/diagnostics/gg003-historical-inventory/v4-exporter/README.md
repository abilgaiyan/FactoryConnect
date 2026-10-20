# V4 SQL-provider adapter and evidence publication — next review

Status: offline implementation candidate; V4 OPEN. No database invocation performed.
PR #141 SQL remains byte-identical: SHA256 dbb7af00aa97241f789d982b43a5740c774a79973360bcb6ba66d51d8cac7719.
The preceding offline core passed 17 tests at commit 1e3cfec21f5828e24a6fec4bb91492491a1a3b2b. Those results DO NOT certify the new adapter/core changes.
The execution environment became unavailable before this increment. New Python tests, C# build/self-tests and Windows tests are authored but UNEXECUTED locally. The dedicated offline CI workflow will report results; no polling/waiting requested.

## Implemented boundary

- SqlReader/Program.cs: direct Microsoft.Data.SqlClient 7.0.2 adapter, .NET 10.
- q2b_schema.py: independently transcribed ordered names/types for all seven SELECTs.
- provider_spool.py: strict framed spool reader, completion/identity/cardinality/count verification.
- evidence_export.py: deterministic evidence writer; live finalization hook before publication, exclusive sibling lock.
- capture_spool.py: offline spool-to-evidence entry point; contains no database connection.
- test_provider_spool.py: synthetic provider frames, delayed errors, schemas, identities and publication cases.
- report_source_hashes.py: offline commit/OS/Python/source hash report.
- .github/workflows/q2b-exporter-offline.yml: Ubuntu/Windows Python tests, C# build and provider-value self-test; NO database calls.

## Provider precision and schemas

C# obtains decimals via GetSqlValue/SqlDecimal (up to 38 digits), never GetDecimal or double. Current culture is invariant. DateTimeOffset uses the provider's seven-digit value and O formatting, never Python datetime microseconds.
SqlDataReader.GetColumnSchema supplies name/type/order, nullability, size, numeric precision and scale. Unknown metadata fails closed. Raw metadata is retained in evidence.
The independent SELECT contract verifies names/types/order; decimal positions must be decimal(20,0), timestamps datetimeoffset(7). Provider nullability MUST be an actual Boolean and is preserved, not inferred. Expression nullability is not assumed from table DDL; exact scalar/cardinality/non-NULL identity checks are applied independently. Provider size/precision metadata is retained even where an expression's precise TDS size awaits validation.
This distinction is explicit: live provider metadata has NOT been observed for this implementation. Any unexpected type/order/scale fails, and provider-specific metadata behavior still needs review.
SERVERPROPERTY returns sql_variant. Only a System.String variant is accepted, retaining its provider type tag and string; arbitrary variant coercion is rejected.

## Terminal command success and failures

Adapter drains all seven sets, including NextResultAsync(false); disposal, connection closure, command disposal and final spool flush must succeed before capture completion receipt.
A shared 60-second cancellation token covers open/read/next-result, invokes SqlCommand.Cancel and uses WaitAsync around finalization; CommandTimeout=60 and ConnectTimeout=15. Errors/timeouts return nonzero with no valid success receipt.
Frames include schema and every row ordinal. Terminal includes SQL hash, prefix hash, row counts, provider identity and authorization ID. A separate .success.json sidecar binds final full-spool SHA256 after spool closure. Missing/partial/mismatched terminal OR sidecar rejects evidence publication.
The offline adapter validates exact server/database/read-only values in target/end rows; expected ORIGINAL_LOGIN; machine/processor/stream/revision identities; singleton target/cut/boundary/coverage/end; boundary/coverage/member counts.
SQL anomalies are retained, not hidden or interpreted as historical truth. OriginalLogin expected value must come from approved execution envelope, never guessed.

## Evidence publication and Windows

Temporary evidence directory is created under destination parent: same-volume rename by construction. An O_EXCL sibling lock serializes cooperating publishers; existing output and stale locks are never overwritten/stolen.
Data/manifest are flushed before rename. Rename/finalizer failures clean temporary paths; interruption before publish cannot create a complete output. A crash may leave incomplete staging/lock artifacts; preserve/review them and never auto-promote or auto-delete another owner's artifacts.
Tests include native filesystem operations, synthetic rename failure, cross-process lock contention and interrupted writer, scheduled on windows-latest and ubuntu-latest. Results remain pending. Hosted Windows evidence does not automatically validate a particular developer drive/filesystem; intended-target filesystem acceptance remains separate.
Uncooperative processes, network shares and power-loss durability are not claimed. Output paths must be dedicated local same-volume directories controlled by the operator.

## Offline invocation candidates (no DB)

From this folder:
```text
python -m unittest -v test_evidence_export.py test_provider_spool.py
python report_source_hashes.py
dotnet build SqlReader/SqlReader.csproj --configuration Release
dotnet run --project SqlReader/SqlReader.csproj --configuration Release --no-build -- --self-test
```
C# self-test exercises provider-value conversion only, not a real SqlDataReader. Python frames exercise the adapter consumer and terminal/publication gates, not SqlClient network cancellation.
Do not call adapter without --self-test during offline validation. Default invocation is disabled.
New source hashes and exact commit must be recorded from report_source_hashes.py on the tested checkout; no uncomputed source hashes are asserted here.

## Future execution envelope — NOT APPROVED / NOT PROVIDED

Adapter's live entry point requires explicit --execute-authorized, a JSON envelope path, frozen SQL path and exclusive spool path. No executable live command or completed envelope is supplied.
Envelope must identify authorized=true, authorization_id, exact server/database, expected_original_login, SQL hash, timeout_seconds=60 and max_spool_bytes=10485760. Restricted Windows integrated identity, immutable READ_ONLY target and existing administrator attestation remain prerequisites. Connection pooling is disabled; ApplicationIntent=ReadOnly is advisory only; SQL's actual role/target guards still run.
Spool completion alone does not publish evidence. Separate offline capture_spool.py requires expected login/authorization ID and independently verifies schemas, terminal/sidecar, singleton values and counts before export.
Export manifest complete=true means complete typed-reader capture ONLY. Historical correctness remains NotEstablished.

## Remaining review gates

1. Execute offline CI; inspect actual C# compile/value tests and Python test results, fixing failures before freeze.
2. Independently review provider metadata mapping and GetSqlValue behavior, including SqlDecimal and datetimeoffset, before live use.
3. Validate genuine command cancellation/disposal error behavior with a separately authorized non-production fixture if synthetic coverage is insufficient; no such database test is authorized by this commit.
4. Review exact intended Windows publication filesystem and incomplete-artifact recovery.
5. Freeze validated commit/source hashes, build artifact/dependency identity, exporter invocation and resource limits in an explicit authorization review.
PR #141 merge, restored database queries, historical extraction, indexes/schema changes, reporting replay and production changes remain HELD.
