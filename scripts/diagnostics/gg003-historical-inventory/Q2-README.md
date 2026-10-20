# Q2A/Q2B — Historical identity, counts and exact-cut membership (review candidate)

Baseline: 65e78b6b82c5930b83f1357e641948c0e836d331 (#139).
Schema source: uploaded Q1 output captured 20 October 2026, Pasted markdown(20261020-122953).md, final target DESKTOP-1PJK2OB\\FCINVENTORY / FactoryConnect_GG003_IMP1C_INVENTORY_20261020 / is_read_only=1.
Q0/Q1 were reported executed PASS; this packet does not repeat installation, restore, permission discovery or schema discovery. File content confirms Q1 result counts 289/52/91/1. Permission rows include repeats; they are not 91 distinct grants.

## Status

The original generic packet is retained below as design/reference material. Q2A execution has since been reported completed and its inventory evidence is preserved; merging does not authorize rerunning it. The generic Q2B script is not authorized for execution. The current revision-534 candidate and offline exporter are distinguished explicitly below.
Inherited reviewed procedure and permission/administrator attestation requirements remain in force. Guards recheck exact target/read-only continuity and fail-closed sysadmin/db_owner status; they do not redo Q0/Q1.
Machine: 677b030b-1ede-4a67-82ae-6a494791357f. Dates: business dates [2026-10-16,2026-10-20).
Target identifiers are explicit from Q1, not inferred from old LocalDB names.

## Current authority and validation checkpoint

- **Q2A — completed inventory evidence:** retain `Q2A-machine-revision-overview.sql` and the previously captured outputs. The reviewed inventory identified stream row 1, processor row 1, 746 revisions from 10 through 2150, and a terminal empty revision page. This is retained discovery evidence, not historical completeness proof.
- **Generic Q2B — reference only:** `Q2B-exact-cut-fact-lineage.sql` is a six-result-set paged design/reference candidate. It is not an alternative execution-authorized packet for revision 534; its parameter and continuation guidance below remains reference material.
- **Revision-534 Q2B — authoritative investigation candidate:** [PR #141](https://github.com/abilgaiyan/FactoryConnect/pull/141) supplies `Q2B-revision-534-lineage-review.sql`, with seven ordered result sets and canonical UTF-8/LF SHA-256 `DBB7AF00AA97241F789D982B43A5740C774A79973360BCB6BA66D51D8CAC7719`. Its lineage, schema and output contract governs the revision-534 investigation. Aggregation revision 534 is the committed metric-input watermark for the exact processor; admission is established by its contribution ledger and complete fact reference, not by stored fact existence alone.
- **V4 exporter — offline validation:** implementation and offline validation gates are reported complete. This validates typed evidence capture and publication tooling, not historical operands or authority. `complete=true` describes complete typed-reader capture only. Actual restricted-reader database execution requires a separate, explicit authorization envelope.

The seven revision-534 sets, in order, are Q2B-target, Q2B-cut, Q2B-contribution-boundary, Q2B-fact-membership, Q2B-duration-operands, Q2B-coverage and Q2B-end-target. Do not feed the generic six-set output to the seven-set exporter.

No new database reads, writes, schema/index changes, reporting replay or production access are authorized by either merge. IMP-1 and historical numeric feasibility remain open / NotEstablished. The following original packet design records its proposed stages and limits; it does not override this checkpoint.

## Original generic review/execution stages

| Packet | Reads | Bound / output | Next gate |
| --- | --- | --- | --- |
| Q2A | Identity rows, committed revisions, machine/date fact counts and activity/raw discovery counts | Max 100 streams/processors; complete per-processor revision totals plus composite-key pages<=500, no historical revision-count cap | Review identity/cut selection before Q2B |
| Q2B | One approved committed cut's complete contribution summary, grouped counts and fact-lineage pages | Explicit processor row/stream row/cut; page size<=200; no automatic latest selection | Reconcile pages before payload-source packet |

Resource review must approve client query timeout (proposed 60 seconds per command), maximum output (proposed 10 MiB), and maximum pages/run (proposed 20) before execution. These client limits are not SQL scan-cost guarantees and are not encoded as silent TOP truncation. On limit/timeout/error, stop with incomplete evidence; no completeness claim. An approved continuation keeps the same immutable copy/cut and last validated position.
Q2A aggregate groups can exceed an output bound even with identity caps; capture failure/truncation is a stop condition, never successful evidence. No unbounded raw observation values or JSON payloads are returned.

Expected Q2A default: eight result sets (target, identities, revision inventory, revision page, fact counts, window-counts-held, page request, end-target). With separately approved IncludeWindowCounts=1: nine result sets, replacing held marker with raw/activity count sets.
Expected Q2B: six result sets (cut, membership summary, period/key counts, lineage page, page progress, end-target).
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
- Q2A: previously completed inventory evidence retained; no new execution authorized by this merge.
- Generic Q2B: reference only, NOT AUTHORIZED. Frozen revision-534 seven-set candidate: selected in PR #141, actual execution separately HELD.
- No new restore, login change, migration, receipt backfill, replay or production action.

## Review corrections and continuation protocol

CutText and AfterPositionText are varchar(max) inputs validated for canonical ASCII digits, length and lexicographic UInt64 bounds before TRY_CONVERT; no numeric assignment overflow or truncation occurs before validation. Reject empty/negative/fractional/exponent/whitespace/leading-zero text and max+1; cut zero is rejected, cursor zero allowed. decimal(20,0) does represent some numbers above UInt64 max, but cannot accept arbitrarily long unchecked input safely.
Required future SQL validation vectors: 0/1/UInt64 max/max+1, 21+ digits, NULL, signs, whitespace, decimal/exponent text and cursor>cut. These have not been executed.

Q2B separately reports missing exact joins, absent referenced fact IDs, existing facts with mismatched stream/position, wrong existing fact machine and unverifiable machine due to absent fact. Categories may overlap; do not sum anomaly counts. A zero wrong-machine count with nonzero unverifiable count is not a clean machine check. LEFT JOIN retains unresolved contributions.

Q2A revision pages use composite cursor (processor row ID, revision position), starting (0,0). Continue from the last returned composite key, never MAX across processors. Preserve every page and reconcile unique per-processor keys with TotalCommittedRevisions; acquire a terminal empty page. The inventory covers all machine revisions, not just changed-day rows.
Q2B retains exact processor/stream/cut and position cursor. The 20-page limit is a per-run pause, not a 4000-row membership cap.
At every pause save a continuation receipt: reviewed commit/parameterized file hash, immutable database/backup identity, machine/source/cut or revision-discovery scope, page sequence, input cursor, returned min/max/key count, cumulative unique count, summary totals and output hashes. Status is Partial until terminal empty page and count reconciliation. Timeout/truncated/failed pages are not committed to the receipt; resume from the last fully validated cursor, deduplicating only identical repeated page exports.
A new run needs explicitly approved continuation bounds and same target provenance/parameters. Any target/summary change, skipped range or conflicting duplicate stops as Inconsistent; do not silently restart with latest data. Full completion is recorded by the reviewer as Complete only after these checks; SQL progress markers conservatively remain PARTIAL.

Raw/activity envelope counts default disabled. Enabling requires independent index and estimated-plan review in a separately permitted non-production environment; Q1's selected index output did not establish MachineObservation's access path. Review seek/scan predicates, rows read, sort/memory estimates and machine-prefix/date filtering. Activity's stream-position index does not guarantee selective StartedAt/EndedAt filtering; fact/date grouping and revision totals also require cost review.
Before ANY execution freeze exact command, commit, target, operator, timeout, output path/byte bounds and per-run page limit. No claim that date predicates or TOP page sizes bound physical I/O. No index changes/hints or runtime test are authorized here.
Focused SQL Server syntax/estimated-plan validation remains pending separate permission; no connection, plan compilation or query execution has occurred. Repository CI alone does not certify SQL syntax/plans. Q2A-only review/authorization precedes selected Q2B parameters and execution.
