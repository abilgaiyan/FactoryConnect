# FC-029 — First Factory Dashboard / Daily Report

## Status

- **FC-029.1 — Dashboard Application Foundation:** complete
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
  - **FC-029.1G — architecture, deployment, and whole-feature conformance:** complete

## FC-029.1 closure evidence

Final regression evidence for the completed Dashboard Application Foundation:

```text
frontend       108/108 passed
non-SQL        783/783 passed
SQL Server      75/75 passed
Release build  passed
git diff --check clean
working tree   clean
branch         synchronized with origin
```

The final closure head must also pass `npm run contracts:check` so the committed generated FC-028 client contract remains deterministic, type-safe, and synchronized. No additional code or documentation changes should follow that final contract check before closure confirmation.

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

`FactoryConnect.Dashboard` has no FactoryConnect project or runtime assembly dependency. Browser transport contracts come from generated FC-028 OpenAPI types.

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

SQL Server does not yet provide the FC-027 operational-metric projection/query capability. The dashboard does not bypass that limitation by reading persistence directly or weakening provider validation.

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

The dashboard uses the pinned React/Vite stack and produces hashed production assets into the dashboard web root. Frontend TypeScript and Node/Vite configurations remain strict and separated.

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

The upstream `ReportingApiBaseAddress` is never exposed to the browser.

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

Routing is exact, query/fragment independent, safely URI-decoded, history-aware, and explicitly root-hosted. Reserved host paths do not fall through to the SPA shell.

### E.4 — QueryState and lifecycle controller

The closed presentation lifecycle vocabulary is:

```text
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

Vite production assets are generated into `wwwroot`, included in clean `dotnet publish`, and served by the dashboard host. Generated web assets are not repository source-of-truth files.

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

The accepted one-day query range is deliberately narrower than JavaScript `Date` so every accepted selection is representable by ASP.NET `DateOnly` and has a representable exclusive next day:

```text
0000-01-01 → invalid
0001-01-01 → valid
9999-12-30 → valid
9999-12-31 → invalid
```

Calendar advancement is UTC/calendar based and does not infer factory timezone boundaries.

The result presentation preserves configured display name mapped by exact source identity; top-level source identity; site/business date; metric key/version; status; nullable value; unit; reason; production context; complete source revision; and continuation-token presence.

Unknown response sources remain visible by authoritative identifiers. Calculated zero renders as zero. `unavailable` and `insufficient-evidence` remain distinct with supplied reasons. Pagination is intentionally inactive in the first proof, but continuation-token presence is surfaced rather than discarded.

Route changes and unmount use E.5 ownership/cancellation semantics; a late obsolete production-day response cannot publish.

## FC-029.1G — architecture, deployment, and whole-feature conformance

G is a closure slice and introduces no new dashboard capability.

The completed conformance proves:

1. **Documentation closure** — E.1 through E.6, F, G, and parent FC-029.1 are recorded complete; LAN deployment is documented separately.
2. **Forbidden reference boundary** — `FactoryConnect.Dashboard` has no FactoryConnect project or runtime assembly dependency.
3. **Clean publish and fail-closed deployment** — clean Release publish produces the React entry and hashed asset; repository defaults contain no usable upstream/source settings; production accepts valid non-loopback LAN composition.
4. **Exact gateway restriction** — only the two exact FC-028 POST routes forward; non-POST methods, unknown paths, extra segments, shortened paths, and trailing-slash variants do not become gateway routes.
5. **Seven-source composition** — seven unique `(MachineId, ProcessorId)` identities project exactly to browser runtime configuration while the private upstream address remains absent.
6. **UI state distinctions** — `empty`, `invalidRequest`, `failed`, calculated zero, `unavailable`, and `insufficient-evidence` remain semantically distinct; unknown sources and continuation-token presence remain visible.
7. **Whole-feature regressions** — frontend, Release build, non-SQL, SQL Server integration, Git cleanliness, and generated-contract synchronization are required closure gates.

Deployment and hosting details are documented in:

```text
docs/deployment/factoryconnect-dashboard-lan.md
```

That deployment boundary includes explicit production environment selection, Kestrel LAN binding through `ASPNETCORE_URLS`, Dashboard environment-variable mapping, factory-LAN-restricted firewall exposure, and deployment-infrastructure ownership of TLS termination, certificate lifecycle, service supervision, restart policy, and host hardening.

## FC-029.1 outcome

FC-029.1 establishes the production-capable dashboard application foundation and one authoritative production-day reporting vertical without inventing dashboard-side factory semantics.

No metric cards, charts, factory-wide OEE aggregation, current-machine-state dashboard, or printable daily report is introduced by FC-029.1. Those remain later FC-029 slices built on this closed foundation.
