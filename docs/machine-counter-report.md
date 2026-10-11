# Raw counter inspection

The machine detail page provides a read-only counter inspection below activity history. It is a quantity-discovery surface, not a production-admission processor or OEE repair.

## Inputs and read boundary

`POST /api/machines/v1/{machineId}/counter/query` accepts positive half-open UTC bounds of at most 31 days and an exact, case-sensitive source/address pair. The form defaults to today's IST calendar date and editable `mtconnect` / `part_count` examples; these do not designate a production quantity mapping. Returned numeric signal names help select the deployed raw address. Numeric signals may also represent speed or feed.

The single configured machine owner selects the observation stream. A stable authority read pins its acquisition revision and raw accepted-through position. State evaluation does not select the raw boundary. The reader is the selected persistence provider's own observation store; no alternate provider or fixture reader is used in production.

Raw history is traversed in durable position order through the exact boundary, retaining signal predecessors outside the selected time window. Post-boundary observations are excluded. Missing authority returns 503; malformed selection returns 400; reader/integrity failures return operation failure, never empty results. Full-prefix traversal is capped at 1,000,000 observations and selected samples at 5,000. Exceeding either limit returns 422 without partial results. Narrowing dates does not reduce the full-prefix scan; a future indexed reader is needed for larger histories.

## Evidence and flags

Raw values are invariant strings, avoiding JavaScript integer precision loss. Rows retain quality, signal type, exact UTC timestamp, position, instance, sequence and predecessor position. IST formatting is presentation only.

Arithmetic differences are displayed only between adjacent observations of the exact signal with the same instance, strictly increasing observation time, Good quality and nonnegative integral decimal values. Decreases are flagged `CounterDecreased` and receive no difference. This establishes a decrease, not its cause or a confirmed reset. Instance changes, missing values, unusable predecessors and non-increasing timestamps receive explicit flags and no difference.

Every row remains `ContinuityNotEstablished` and `AttributionNotEstablished`. No counter increment is admitted as production, and no shift/day quantity total is produced. A successful empty exact-signal selection is not zero production or proof of an acquisition gap. Sequence gaps between counter samples are not used to infer omissions: other signals share the stream.

Only table rows are paged (25/50/100, default 50), with reload and page-size reset. Failed, timed-out and superseded requests cannot publish partial or late results. Available signal names and loaded metadata remain bound to the completed report when form fields change.

## Quality policy boundary

Management requested that good quantity default to admitted produced quantity and rejected quantity to zero. That is an all-good policy assumption, distinct from measured good/rejected evidence. This report labels that distinction but does not apply the policy, alter stored evidence, or publish Quality/OEE. Missing or zero production does not become 100% Quality.

## Conformance

Added Core tests for exact values, signal isolation, predecessor selection, decreases, instance changes, missing/poor-quality evidence, timestamp ordering and boundary integrity. Added API tests for raw boundary exclusion, missing authority, later-page failure, premature history end and invalid selection. Frontend tests check exact references, timestamp precision, signal binding, missing values, failed retrieval and cancellation. Full .NET/frontend/typecheck/build/SQL validation runs in remote CI; results must be recorded against the exact tested commit.

No migration, factory operation, schedule change, projection change or R3243 repair is included. R3243 remains Unproven.
