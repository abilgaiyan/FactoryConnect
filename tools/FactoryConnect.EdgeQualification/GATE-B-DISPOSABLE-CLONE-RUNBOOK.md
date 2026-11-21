# PR #180-B — disposable SQL clone preflight runbook

Status: PREPARATION ONLY. This document authorizes no database creation, fixture admission, production deployment or service start.

## Boundary

Run only on the local development SQL instance `DESKTOP-1PJK2OB\FCINVENTORY`. The target must be a **new** database named `FactoryConnect_P0_GateB_Fixture_20261110`, restored from an approved verified backup. Do not reuse `FactoryConnect`, `FactoryConnect_P0_20261105`, `FactoryConnect_P0_Qualification_20261106`, or `FactoryConnect_P0_GateB_20261109`. Do not run production Edge or an acquisition writer.

## Operator preflight

1. Confirm that both Edge hosts are stopped and record their process/service status. Do not start them for this phase.
2. Verify the source backup and record its absolute path, size, checksum and backup verification result. The production backup path is not assumed to be accessible from the local SQL instance.
3. Inspect SQL backup file metadata and logical file names using read-only SQL commands. Choose new data/log file paths under the local development instance. Do not use `WITH REPLACE`; do not overwrite an existing database or file.
4. Create the **new** disposable clone only after checking the target database does not exist. Record restore command, start/end UTC, operator and exact SQL server. Never attach the original incident database files.
5. Open a query window connected to the new clone. Run `GateB-DisposablePreflight.sql` unchanged. Save all result sets and any error number/message to a new immutable report file. Exit codes 51100–51112 are rejection, not permission to change the baseline.
6. Confirm the query reports `READ_ONLY_BASELINE_MATCH; NOT AN ADMISSION AUTHORIZATION`. Save the actual publication head identity hash and activity/checkpoint snapshots. A structural match is not cryptographic chain verification.
7. Before any writer, independently validate the entire retained publication chain with the production `IAcquisitionEvidenceReader` and canonical codec. Verify commissioned mapping, source identity, configuration SHA-256, exclusive writer and fixture-owned new instance. No SQL `INSERT`/`UPDATE` of raw observations, checkpoints, evidence or activity authority.
8. Keep the clone untouched if the chain cannot be validated or if any baseline differs. Report **BLOCKED** with evidence; do not reset a checkpoint to force a pass.

## Required evidence to return

- SQL instance and database names, backup checksum and verification result, restore command with paths redacted if sensitive
- Exact preflight script commit and execution timestamp
- Every preflight result set or SQL error
- Separate cryptographic publication-chain verification output, if available
- GG-003 and MOXA-001 authority/checkpoint snapshots, with a clear statement that no writer ran

## Acceptance interpretation

A successful preflight is **only** clone identity and structural baseline qualification. Gate B requires actual admitted MOXA state transitions, position **and** revision advancement, GG chronology hold preservation, exact replay/conflict tests, and a separate full host restart with a second eligible MOXA transition. A host exit code 3 is inconclusive, not PASS.

Production deployment, merge approval and historical evidence recovery remain on hold until the independent acceptance gates are satisfied.
