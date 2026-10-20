# Q2A/Q2B — Historical identity, counts and exact-cut membership (review candidate)

Baseline: 65e78b6b82c5930b83f1357e641948c0e836d331 (#139).
Schema source: uploaded Q1 output captured 20 October 2026, Pasted markdown(20261020-122953).md, final target DESKTOP-1PJK2OB\\FCINVENTORY / FactoryConnect_GG003_IMP1C_INVENTORY_20261020 / is_read_only=1.
Q0/Q1 were reported executed PASS; this packet does not repeat installation, restore, permission discovery or schema discovery. File content confirms Q1 result counts 289/52/91/1. Permission rows include repeats; they are not 91 distinct grants.

## Status

Draft SQL/design only. No database connection/execution or SQL Server syntax/performance validation performed. Creating/merging this packet does not authorize Q2 execution.
Inherited reviewed procedure and permission/administrator attestation requirements remain in force. Guards recheck only exact target/read-only continuity; they do not redo Q0/Q1.
Machine: 677b030b-1ede-4a67-82ae-6a494791357f. Dates: business dates [2026-10-16,2026-10-20).
Target identifiers are explicit from Q1, not inferred from old LocalDB names.

## Separate review/execution stages

| Packet | Reads | Bound / output | Next gate |
| --- | --- | --- | --- |
| Q2A | Identity rows, committed revisions, machine/date fact counts and activity/raw discovery counts | Max 100 streams/processors, 2000 machine revisions; abort rather than truncate identity/revision lists | Review identity/cut selection before Q2B |
| Q2B | One approved committed cut's complete contribution summary, grouped counts and fact-lineage pages | Explicit processor row/stream row/cut; page size<=200; no automatic latest selection | Reconcile pages before payload-source packet |

Resource review must approve client query timeout (proposed 60 seconds per command), maximum output (proposed 10 MiB), and maximum pages/run (proposed 20) before execution. These client limits are not SQL scan-cost guarantees and are not encoded as silent TOP truncation. On limit/timeout/error, stop with incomplete evidence; no completeness claim. An approved continuation keeps the same immutable copy/cut and last validated position.
Q2A aggregate groups can exceed an output bound even with identity caps; capture failure/truncation is a stop condition, never successful evidence. No unbounded raw observation values or JSON payloads are returned.

Expected Q2A: seven result sets (target, identities, revisions, fact counts, raw window counts, activity window counts, end-target).
Expected Q2B: five result sets (cut, membership summary, period/key counts, lineage page, end-target).
No specific source row counts or actual historical cut values are asserted before execution.
Use sqlcmd error handling (-b) in a separately reviewed invocation; DECLARE/THROW are session-only control, no DDL/DML/EXEC/SELECT INTO or application bootstrap.

## Semantics and integrity

Q2A current checkpoint is a diagnostic, not historical completeness. ChangedDayCount records changed period membership, not all periods represented at the cut. Revision 534 is a candidate only.
Raw/activity UTC envelope [2026-10-15T18:30Z,2026-10-20T00:30Z) is a discovery window, not authoritative production-day membership. Machine raw positions and metric-input positions stay separate.
MetricValue is returned exactly as stored text, not cast/summed into a replacement Availability. Eligibility bits/assignment IDs remain evidence fields, not authority admission.
Q2B LEFT JOIN preserves missing fact joins. Wrong stream/machine or unresolved membership must be investigated before payload verification. Nullable source references are counted per key; quantity facts can legitimately differ from duration facts.
Stable contribution position paging uses the unique processor/position key discovered by Q1. Start AfterPosition=0, advance only to max returned position, preserve all pages through an empty page, reconcile unique count to ContributionCount. Do not silently skip pages, orphan rows or other keys/dates.
All stored period/site/shift identities remain in output; date labels alone cannot select a unique reporting scope.

## Known schema boundary and deferred source families

Q1 shows context/eligibility outputs are JSON payload stores with hashes but no MachineId column. Do not count their entire tables as GG-003 or parse guessed JSON paths. Next packet resolves exact source IDs from approved Q2B membership and verifies original payload identity/content.
Q1's filtered metadata did not show acquisition publication tables. This does not prove they never existed: visibility/version/artifact checks remain open; this packet does not query guessed optional tables.
Schedule roster structure does not establish complete POT-1/break/override authority. External schedule provenance remains separate.
Coverage subject/version structures exist, but exact subject/revision payload verification is deferred; no Complete label can fill missing authority.

After Q2 results review, prepare Q2C for exact referenced output payloads, schedule/roster candidates, activity continuity/acquisition retention and coverage dependencies. Preserve full original payload bytes in an approved export format with output-size controls and versioned offline verification. Current plain-text lineage output is not a canonical binary archive.
Q2A/B can establish retained row/membership diagnostics, not producer-batch closure, planned completeness or numeric Availability. IMP-1 and historical numeric feasibility remain NotEstablished pending actual evidence and review.

## Execution authorization fields

- Exact reviewed commit and locally parameterized file hashes: pending review.
- Q2A exact SQL, target, operator, output path, timeout/byte bounds: NOT AUTHORIZED.
- Q2B processor row/stream row/cut and run/page limit: NOT SELECTED / NOT AUTHORIZED.
- No new restore, login change, migration, receipt backfill, replay or production action.
