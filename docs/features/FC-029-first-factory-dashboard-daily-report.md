# FC-029 — First Factory Dashboard / Daily Report

## Status

- **FC-029.1A — merged FC-028 contract inventory:** complete
- **FC-029.1B — dashboard host foundation:** complete
- **FC-029.1C — OpenAPI → TypeScript generation:** complete
- **FC-029.1D — typed reporting client boundary:** complete
- **FC-029.1E — React application shell and query state:** complete
  - **FC-029.1E.1 — React/Vite build foundation:** complete
  - **FC-029.1E.2 — runtime configuration and same-origin gateway:** complete
  - **FC-029.1E.3 — deterministic application router and shell:** complete
  - **FC-029.1E.4 — QueryState and lifecycle controller:** complete
  - **FC-029.1E.5 — cancellation and stale-response conformance:** complete
  - **FC-029.1E.6 — shell integration and production asset build:** complete
- **FC-029.1F — first vertical reporting proof:** complete
- **FC-029.1G — architecture, deployment, and whole-feature conformance:** active

## Architectural invariant

The dashboard consumes reporting contracts. It does not reconstruct factory metrics, reporting periods, production context, current machine state, or reporting persistence.

```text
FC-027 durable operational metric projections
    ↓
FC-028 Reporting API
    ↓ HTTP/OpenAPI
FactoryConnect.Dashboard
    ↓
presentation only
```

`FactoryConnect.Dashboard` must not reference FactoryConnect Core, Edge, reporting abstractions, persistence providers, SQL Server, protocol runtimes, aggregate readers, observation stores, or `FactoryConnect.Api` merely to reuse types. Browser transport contracts come from generated FC-028 OpenAPI types.

The dashboard may format, group, filter, navigate, and render authoritative reporting results. It must not calculate Availability, Performance, Quality, OEE, utilization, production-day boundaries, current machine state, or factory-wide percentages. It must not combine metric-definition versions or reinterpret reporting absence/failure states.

## FC-029.1A — authoritative FC-028 inventory

FC-028 exposes two reporting operations:

```text
POST /api/reporting/v1/operational-metrics/shifts/query
POST /api/reporting/v1/operational-metrics/production-days/query
```

Source identity is exactly `(MachineId, ProcessorId)`. Successful no-match queries return an empty page. Transport statuses are exactly:

```text
calculated
unavailable
insufficient-evidence
```

Calculated zero remains distinct from unavailable or insufficient evidence.

SQL Server still does not provide the FC-027 operational-metric projection/query capability. The dashboard does not bypass this limitation by reading persistence directly or weakening provider validation.

## FC-029.1B — dashboard host foundation

`FactoryConnect.Dashboard` is an ASP.NET Core factory-LAN host with:

```text
DashboardOptions + startup validation
/health/live
/health/ready
static production asset hosting
SPA fallback for presentation routes only
```

Production configuration intentionally contains no usable reporting endpoint or source defaults. Startup requires an absolute HTTP/HTTPS reporting address, a positive timeout no greater than five minutes, at least one source, non-empty source identity/display values, and unique `(MachineId, ProcessorId)` pairs. Production rejects loopback reporting addresses.

Health semantics remain narrow:

```text
/health/live  → process is running
/health/ready → frontend index asset exists
```

Neither endpoint probes FC-028.

## FC-029.1C — OpenAPI → TypeScript generation

The browser reporting contract is generated from the authoritative FC-028 OpenAPI document. A normal `FactoryConnect.Api` build emits the intermediate OpenAPI document and `openapi-typescript` generates:

```text
src/FactoryConnect.Dashboard/ClientApp/src/api/generated/reporting-contract.ts
```

The generated artifact is committed, marked generated, and checked for deterministic regeneration and drift through:

```text
npm run contracts:check
```

Node is pinned to `24.14.1`. TypeScript is strict. Generated transport contracts are not manually patched with additional reporting semantics.

## FC-029.1D — reporting client boundary

The browser-side `ReportingClient` executes only the two authoritative FC-028 operations.

```text
createReportingClient(options)
        ↓
raw HTTP transport
        ↓
request executor
        ↓
response decoder
        ↓
generated OperationalMetricPage
```

The client owns URI composition, request serialization, timeout, caller cancellation, network classification, HTTP/protocol decoding, and runtime response shape validation. It does not own metric calculations or presentation semantics.

Known FC-028 invalid-query Problem Details remain distinct from malformed/incompatible continuation-token failures. Unknown valid Problem Details remain HTTP failures. Calculated zero, nullable reasons, full source revision, context, and opaque continuation tokens are preserved.

## FC-029.1E — React application shell and query state

### E.1 — React/Vite build foundation

The dashboard uses the pinned React/Vite stack and produces hashed production assets into the dashboard web root. The frontend TypeScript and Node/Vite configurations are separated and strict.

### E.2 — runtime configuration and same-origin gateway

The browser loads only:

```text
GET /dashboard/config
```

Browser-safe runtime configuration contains:

```text
reportingBasePath = "/"
requestTimeoutMilliseconds
sources[] = MachineId + ProcessorId + DisplayName
```

The upstream `ReportingApiBaseAddress` is not exposed to the browser.

The factory pilot uses a same-origin gateway:

```text
browser
  ↓ same origin
FactoryConnect.Dashboard
  ↓ controlled forwarding
FC-028 Reporting API
```

The gateway forwards only the two authoritative FC-028 POST operations. It preserves request bytes and response status/body/content type. It does not retry, cache, reshape, calculate, or classify reporting results. Upstream timeout maps to 504; upstream connection/runtime failure maps to 502; browser cancellation aborts forwarding.

### E.3 — deterministic application router and shell

The root-hosted application route set is:

```text
/
/production-days/{productionDay}
/machines/{machineId}
/production-days/{productionDay}/report
```

Routing is exact, query/fragment independent, safely URI-decoded, history-aware, and explicitly root-hosted. Unknown paths produce the application not-found route. Reserved host paths do not fall through to the SPA shell.

### E.4 — QueryState and lifecycle controller

The closed presentation lifecycle vocabulary is:

```ts
idle
loading
success<T>
empty
invalidRequest
failed
```

Required distinctions:

```text
successful page with zero items → empty
invalid reporting query         → invalidRequest
calculated value = 0            → success containing zero
timeout/network/http/protocol   → failed
failure                         ↛ empty
```

### E.5 — cancellation and stale-response conformance

Each execution owns a generation and `AbortController`. Superseded or disposed requests are aborted, but generation ownership—not cooperative abort behavior—is authoritative for stale publication suppression.

```text
A starts
B supersedes A
A completes late
        ↓
A cannot publish over B
```

Unexpected cancellation of the current request remains a failure. Obsolete programming exceptions remain observable to their awaiting caller but cannot mutate current UI state.

### E.6 — shell integration and production asset build

Runtime configuration is loaded once at bootstrap and the same-origin `ReportingClient` is composed once. React mounts/unmounts controller subscriptions correctly. Every QueryState variant has deterministic presentation without metric interpretation.

Vite production assets are generated into `wwwroot`, included in clean `dotnet publish`, and served by the dashboard host. The committed placeholder frontend was removed; generated web assets are not repository source-of-truth files.

## FC-029.1F — first vertical reporting proof

The first real reporting surface is explicit:

```text
/production-days/{productionDay}
```

No default production day is inferred.

The vertical path is:

```text
route productionDay
        ↓
strict queryable DateOnly validation
        ↓
FromInclusive = productionDay
ToExclusive   = next calendar date
Sources       = configured (MachineId, ProcessorId) pairs
Order         = period-ascending
Metrics       = null
Context       = null
Statuses      = null
        ↓
ReportingClient.queryProductionDayMetrics()
        ↓
QueryLifecycleController
        ↓
QueryState<OperationalMetricPage>
        ↓
authoritative result presentation
```

The accepted one-day query range is deliberately narrower than JavaScript Date so every accepted selection is representable by ASP.NET `DateOnly` *and* has a representable exclusive next day:

```text
0000-01-01 → invalid
0001-01-01 → valid
9999-12-30 → valid
9999-12-31 → invalid
```

Calendar advancement is UTC/calendar based and does not infer factory timezone boundaries.

The result presentation preserves:

```text
configured display name mapped by exact source identity
top-level MachineId + ProcessorId
SiteId + BusinessDate
MetricKey + DefinitionVersion
Status
nullable Value
Unit
ReasonCode + ReasonOperandName
ProductionOrderId + OperationId + PartId + OperatorId
SourceRevision.MachineId
SourceRevision.ProcessorId
SourceRevision.StreamKey
SourceRevision.Position
continuation-token presence
```

Unknown response sources remain visible by authoritative identifiers. Calculated zero renders as zero. `unavailable` and `insufficient-evidence` remain distinct with supplied reasons. Pagination is intentionally inactive in the first proof, but continuation-token presence is surfaced rather than silently discarded.

Route changes and unmount use E.5 ownership/cancellation semantics; a late obsolete production-day response cannot publish.

## FC-029.1G — architecture, deployment, and whole-feature conformance

G is a closure slice. It introduces no new dashboard capability.

### Required conformance

1. **Documentation status**
   - E.1 through E.6 recorded complete.
   - F recorded complete.
   - G recorded as the final active FC-029.1 slice until regression evidence is green.
   - factory-LAN deployment instructions documented separately.

2. **Forbidden reference boundary**
   - `FactoryConnect.Dashboard` has no FactoryConnect project/assembly dependency.
   - no Core, Edge, persistence, SQL Server, protocol, aggregate, observation, or API implementation dependency is added to the dashboard presentation host.

3. **Clean publish and LAN deployment**
   - clean publish emits `wwwroot/index.html` and its referenced hashed JavaScript asset.
   - repository production defaults fail closed.
   - production accepts a non-loopback reporting API and valid source composition.
   - `/dashboard/config` never exposes the upstream reporting address.

4. **Exact gateway restriction**
   - only the two exact FC-028 reporting POST paths are forwarded.
   - non-POST methods are rejected.
   - unknown paths, extra segments, near-miss paths, and trailing-slash variants are rejected.
   - request/response pass-through, 502/504 behavior, cancellation, and upstream base-path preservation remain covered.

5. **Seven-source composition fixture**
   - production configuration with seven unique `(MachineId, ProcessorId)` pairs starts successfully.
   - browser runtime configuration projects all seven identities and display names exactly.
   - the private upstream reporting address remains absent from browser configuration.

6. **UI absence/failure distinctions**
   - empty page remains `empty`.
   - invalid query remains `invalidRequest` with original Problem Details.
   - timeout/network/HTTP/protocol failures remain `failed` and never `empty`.
   - calculated zero remains successful zero.
   - unavailable and insufficient-evidence remain distinct authoritative statuses.
   - unknown source identity remains visible.
   - continuation-token presence is preserved.
   - no metric arithmetic or factory-wide aggregation is introduced.

7. **Fresh regressions before closure**
   - frontend `npm test`.
   - frontend `npm run typecheck`.
   - frontend `npm run build`.
   - frontend `npm run contracts:check`.
   - Dashboard host/conformance tests.
   - full non-SQL FactoryConnect regression suite.
   - SQL Server integration regression suite.
   - `git diff --check` clean.
   - clean working tree, branch synchronized with remote.

Deployment details and the seven-source shape fixture are documented in:

```text
docs/deployment/factoryconnect-dashboard-lan.md
```

## FC-029.1 closure condition

FC-029.1 — Dashboard Application Foundation is complete only after FC-029.1G receives fresh green frontend, non-SQL, and SQL Server regression evidence and no architecture/deployment blocker remains.

No metric cards, charts, factory-wide OEE aggregation, current-machine-state dashboard, or printable daily report is introduced by FC-029.1. Those remain later FC-029 slices built on this closed foundation.
