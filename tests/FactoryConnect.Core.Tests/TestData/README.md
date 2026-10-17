# Revision-534 sanitized offline regression evidence

This is a field-preserving, identity-sanitized derivative of the retained local SQL evidence, not a synthetic reconstruction. The complete original read-only cut contains 167 facts; selecting duration.running and duration.planned-production yields exactly 34 contributions (31 running, 3 planned). The original offline fixture matched that selection exactly. The local CSV independently agrees on every exported operand field. The raw SQL JSON result matches the normalized 167-fact source.

The fixture keeps exact input positions, decimal strings, units, UTC interval boundaries, business date, eligibility flags, ownership, and source relationships. Company/site/line/machine/schedule/context/assignment identifiers and fact/activity/eligibility identifiers use deterministic aliases. Shared original identifiers keep shared aliases. SQL surrogate row IDs and binary fact-identity encodings are omitted; neither is required for aggregation or provenance relationships. No missing contribution was created. Date/time and duration evidence is deliberately retained because it is necessary to reproduce this failure.

The adjacent source-cut manifest records revision 534, the aliased processor/machine/stream and period, counts, the original source artifact digest, and the sanitized fixture digest. The original source digest is a chain-of-custody fingerprint, not proof of independent factory approval. Original exports, pre-sanitization artifacts, an identity map and field-by-field validation results remain local under gg003-local-only/ and are excluded from this commit. Original production IDs cannot be recovered from the committed aliases alone.

Source artifact SHA256: a4d8dda21b81c4f66b1e4664e65dd2c8bca8be21e6d0e58113efcf84b60502f9
Sanitized fixture SHA256 (UTF-8, LF canonical text; checkout CRLF is normalized by the integrity test): d86b0d6a2991b3e49f3a7e58cfc61fbfe78a15bff4db273e40bb4244c8615653

The tests do not connect to SQL. Counterfactual eligibility, valid/zero/missing operands and codec transport cases are explicitly separate in-memory contract probes. Passing Stage A tests characterizes the current failure; it does not establish implemented containment, live SQL persistence compatibility for a new reason, schedule authority, recovery, or deployment readiness.
