# FC-029 — First Factory Dashboard / Daily Report

## Status

- **FC-029.1A — merged FC-028 contract inventory:** complete
- **FC-029.1B — dashboard host foundation:** complete
- **FC-029.1C — OpenAPI → TypeScript generation:** complete
  - **FC-029.1C.1 — deterministic build-time OpenAPI extraction:** complete
  - **FC-029.1C.2 — pinned minimal Node/TypeScript toolchain:** complete
  - **FC-029.1C.3 — authoritative TypeScript reporting contract:** complete
  - **FC-029.1C.4 — typecheck, drift, and determinism conformance:** complete
- **FC-029.1D — typed reporting client boundary:** active
  - **FC-029.1D.1 — generated type aliases and client contracts:** complete
  - **FC-029.1D.2 — URI composition and request execution:** next

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

`FactoryConnect.Dashboard` must not reference FactoryConnect Core, Edge, reporting abstractions, persistence providers, SQL Server, protocol runtimes, aggregate readers, observation stores, or the FC-028 application project merely to reuse types.

## FC-029.1A — authoritative FC-028 inventory

FC-028 exposes two versioned reporting operations:

```text
POST /api/reporting/v1/operational-metrics/shifts/query
POST /api/reporting/v1/operational-metrics/production-days/query
```

Source identity is `(MachineId, ProcessorId)`. Successful no-match queries return an empty page. Transport statuses are `calculated`, `unavailable`, and `insufficient-evidence`. Calculated zero remains distinct from unavailable or insufficient evidence.

FC-028 currently supports in-memory operational-metric reporting composition. SQL Server does not yet provide the FC-027 operational-metric projection/query capability. The dashboard must not bypass this limitation by reading stores directly or weakening provider capability validation.

## FC-029.1B — dashboard host foundation

The first construction slice establishes only the factory-LAN web host boundary:

```text
FactoryConnect.Dashboard
├── ASP.NET Core shared framework
├── strongly typed DashboardOptions
├── startup validation
├── /health/live
├── /health/ready
├── static file hosting
├── SPA fallback foundation
└── placeholder wwwroot/index.html
```

React, chart packages, reporting HTTP traffic, and presentation view models remain deferred beyond the host foundation.

### Configuration

```json
{
  "Dashboard": {
    "ReportingApiBaseAddress": "http://factory-server:5080",
    "RequestTimeout": "00:00:30",
    "Sources": [
      {
        "MachineId": "11111111-1111-1111-1111-111111111111",
        "ProcessorId": "operational-metrics",
        "DisplayName": "Machine 1"
      }
    ]
  }
}
```

Validation requires an absolute HTTP/HTTPS reporting address, a timeout greater than zero and no more than five minutes, at least one source, non-empty machine and processor identities, required display names, and unique `(MachineId, ProcessorId)` pairs. Production rejects loopback reporting addresses. Production settings intentionally contain no usable machine/source defaults and must be supplied by deployment configuration.

### Health semantics

```text
/health/live   → process is running
/health/ready  → required static frontend entry asset is present
```

Neither endpoint queries FC-028. Downstream reporting API reachability belongs to later client/application behavior.

### SPA fallback boundary

Extensionless presentation routes may fall back to `wwwroot/index.html`. Health, API, configuration, and file-extension paths do not fall through to the SPA so unknown infrastructure paths and missing assets remain 404.

The host does not enable unconditional HTTPS redirection; factory LAN HTTPS termination can be configured explicitly by deployment when available.

## FC-029.1C — OpenAPI → TypeScript generation

The dashboard transport contract is generated from the authoritative FC-028 OpenAPI document. The API contract is not duplicated manually in TypeScript.

### FC-029.1C.1 — deterministic build-time OpenAPI extraction

`FactoryConnect.Api` uses ASP.NET Core build-time OpenAPI generation. A normal API build emits:

```text
src/FactoryConnect.Api/obj/openapi/factoryconnect-api-v1.json
```

The document is intermediate build output and is not committed. Generation exercises the authentic API entry point and endpoint composition; no manually running API host, SQL Server, or contract-generation startup bypass is required.

The emitted `v1` document contains both authoritative reporting operations:

```text
/api/reporting/v1/operational-metrics/shifts/query
/api/reporting/v1/operational-metrics/production-days/query
```

### FC-029.1C.2 — pinned minimal Node/TypeScript toolchain

The frontend contract-generation boundary lives under:

```text
.node-version
src/FactoryConnect.Dashboard/ClientApp/
├── package.json
├── package-lock.json
├── tsconfig.json
└── src/api/generated/
```

Node is pinned to `24.14.1` in the repository-level `.node-version`; `package.json` requires the same exact runtime, and CI reads that version file through `actions/setup-node`. Only `openapi-typescript` and `typescript` are permitted as development dependencies in this slice. Dependency versions are exact and the npm lockfile is committed. TypeScript is configured strictly and emits no JavaScript.

React, Vite, Axios, query libraries, formatters, chart packages, and behavioral HTTP-client code remain out of scope for FC-029.1C.2.

### FC-029.1C.3 — authoritative TypeScript reporting contract

`npm run contracts:generate` performs the full source-of-truth chain from the dashboard client directory:

```text
delete expected intermediate OpenAPI document
        ↓
dotnet build FactoryConnect.Api --no-incremental
        ↓
verify obj/openapi/factoryconnect-api-v1.json exists
        ↓
openapi-typescript
        ↓
prepend generated-file marker
        ↓
src/api/generated/reporting-contract.ts
```

The generation script resolves only repository-relative paths, uses Node path/URL APIs for Windows and Linux compatibility, fails if API extraction or generation fails, and cannot satisfy generation from a stale pre-existing OpenAPI document.

Generated transport/path types remain under `src/api/generated/`. Handwritten compile-time conformance assertions live outside that directory and verify the reporting paths, source identity, metric definition identity, context vocabulary, nullable metric value, exact reporting status/order/scope vocabularies, reason information, source revision, and continuation token without introducing request execution behavior.

The HTTP transport vocabulary is defined once by the API transport layer and reused by runtime request parsing, response formatting, and OpenAPI schema metadata so those surfaces cannot independently drift.

If an authoritative API semantic is not represented strongly enough by the generated OpenAPI contract, the API OpenAPI metadata must be corrected at the source. Generated TypeScript must not be semantically patched or rewritten afterward.

### FC-029.1C.4 — typecheck, drift, and determinism conformance

`npm run contracts:check` is the local and CI conformance gate. It reads the committed generated artifact from `HEAD`, performs two fresh generations, runs strict TypeScript compilation, compares the two generated byte streams for determinism, and compares the final generated output with the committed artifact for drift.

```text
HEAD reporting-contract.ts
        ↓
contracts:generate #1
        ↓
strict tsc --noEmit
        ↓
contracts:generate #2
        ├── generation #1 == generation #2
        └── generation #2 == committed HEAD artifact
```

The comparison is independent of unrelated working-tree changes and cannot be bypassed by editing the generated file before running the check.

CI executes the same boundary on pull requests and main-branch builds:

```text
npm ci
npm run contracts:check
```

No React runtime, HTTP request behavior, query-state behavior, or presentation framework is introduced by FC-029.1C.

## FC-029.1D — Reporting Client Boundary

The handwritten browser-side reporting boundary executes only the two authoritative FC-028 reporting operations. It owns HTTP transport behavior but no metric, reporting-period, production-context, or presentation semantics.

### FC-029.1D.1 — generated type aliases and client contracts

The public TypeScript vocabulary is mechanically derived from the generated OpenAPI path operations. Request DTOs, successful page responses, and Problem Details are not reconstructed by hand.

```text
generated reporting paths
        ↓
operation-derived request/response aliases
        ↓
ReportingClient
        ├── queryShiftMetrics(...)
        └── queryProductionDayMetrics(...)
```

The client construction contract exposes explicit base-address, timeout, and injectable-fetch dependencies, while request options expose caller cancellation through `AbortSignal`. D.1 introduces no HTTP execution, retries, caching, response decoding, presentation state, or React dependencies.

## Next slices

```text
FC-029.1D.2  URI composition and request execution
FC-029.1D.3  cancellation, timeout, and failure taxonomy
FC-029.1D.4  runtime response and Problem Details decoding
FC-029.1D.5  full client conformance and documentation
FC-029.1E    React application shell and query state
FC-029.1F    representative HTTP → presentation vertical proof
FC-029.1G    architecture and deployment conformance
```