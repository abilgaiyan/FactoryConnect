# Machine activity history report

The machine detail page now offers an explicit date-range read of closed persisted activity intervals. Reach it by selecting a machine on Fleet status. Dates default to the current IST civil date and are editable. Selection is 1–31 inclusive IST calendar days; these are not production-day identities.

## Evidence behavior

- The API resolves exactly one configured activity owner and pins its evaluated-through observation position. This is a state-processing checkpoint, not metric aggregation revision R3243.
- Historical activity is traversed in durable position order, filtered through that checkpoint, and clipped to the selected UTC half-open interval. Original interval bounds, durable position, instance and sequence remain in the response and interval details.
- Each recorded state retains its own duration. Unknown intervals are distinct from time without a stored interval. Open current-state intervals are not extended to now.
- Overlaps and inconsistent identities/positions fail the operation. Reads that fail or exceed limits return no report. The report does not certify continuous acquisition or historical coverage.
- Totals are durations of stored intervals, not OEE operands or evidence of planned eligibility. Freshness remains in the separate current-state panel.
- Limits: 31 selected days, 100,000 traversed history records, 5,000 selected intervals. The existing reader traverses the complete historical prefix; very large histories need a future indexed date reader. A smaller date range does not necessarily reduce prefix traversal.

## HTTP

`POST /api/machines/v1/{machineId}/activity/query`

```json
{"fromInclusive":"2026-10-05T18:30:00.000Z","toExclusive":"2026-10-06T18:30:00.000Z"}
```

This selects 6 October 2026 in IST. Response identity includes machine, state processor, observation stream and pinned checkpoint. Duration ticks are 100 ns units; all 31-day duration values fit within JavaScript's exact integer range. Position/instance/sequence values remain strings. The separate `activity-v1` OpenAPI document describes this endpoint; the existing reporting document is unchanged.

## Validation

Focused frontend evidence/date/transport tests ran here: 5 passed. Core clipping/overlap/checkpoint tests and API composition tests are included. The authoring environment has no .NET SDK or installed React/TypeScript toolchain; full compilation, mounted UI validation and live SQL snapshot validation remain required.

Run on a developer machine:

```powershell
dotnet build FactoryConnect.sln --configuration Release
dotnet test FactoryConnect.sln --configuration Release --no-build --filter "Category!=SqlServerIntegration"
Push-Location src/FactoryConnect.Dashboard/ClientApp
npm ci
npm test
npm run typecheck
npm run contracts:check
npm run build
Pop-Location
```

Then run the API against the developer snapshot and the dashboard, open Fleet status → M01, select 04/10/2026 through 06/10/2026 and load activity. Verify timestamps, interval details, readable narrow-screen/table scrolling, failures, and unchanged metric checkpoints. No migration is needed. No Availability repair, quality default policy, quantity admission or schedule edits are part of this slice.
