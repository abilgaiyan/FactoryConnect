# RC2 isolated validation batch contract

Original failure: commit `95457396b9665d0ff8edf1809726b98bd0cee0a4`, [CI run 39047365520](https://github.com/abilgaiyan/FactoryConnect/actions/runs/39047365520). All three probes failed during schema setup with invalid-column errors. This evidence remains in Git history and CI; it is not reclassified as a passing run.

Revision: only explicit batch delimiters are added to the proposed DDL resource. Removing those delimiters yields exactly the previous SQL text. No constraint, key, type, trigger body, policy or canonical byte contract is changed.

Execute all 12 batches sequentially on the same connection and SqlTransaction:

1. Add processor columns.
2. Add processor CHECK and UNIQUE constraints.
3. Create witness archive.
4. Create result archive.
5. Create witness-reference archive.
6. Add retained-projection columns.
7. Drop superseded retained-projection constraints.
8. Add retained-projection constraints and foreign keys.
9. Replace the complete evidence subtype CHECK.
10. Create witness immutability trigger.
11. Create result immutability trigger.
12. Create witness-reference immutability trigger.

Only the outer caller commits or rolls back. There is no per-batch transaction, connection change or commit. The rollback test executes all batches, rolls back and checks that archive tables and the new projection column are absent, then reapplies and commits. The baseline 001–020 migration history must remain identical.

The full evidence CHECK is read from SQL metadata after applying the proposal and exercised with component, codec-1 and codec-2 branches plus invalid nullable, mixed-subtype, timestamp and codec cases. Sentinel payloads test SQL shape only. They do not establish application codec or historical authority compatibility.

Revised execution results are recorded separately in the PR description and CI. C1–C8 remain outside this schema checkpoint. No production migration is registered.
