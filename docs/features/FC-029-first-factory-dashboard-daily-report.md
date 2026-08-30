# FC-029 — First Factory Dashboard / Daily Report

## Status

- **FC-029.1A — merged FC-028 contract inventory:** complete
- **FC-029.1B — dashboard host foundation:** complete
- **FC-029.1C — OpenAPI → TypeScript generation:** active
  - **FC-029.1C.1 — deterministic build-time OpenAPI extraction:** complete
  - **FC-029.1C.2 — pinned minimal Node/TypeScript toolchain:** active

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
src/FactoryConnect.Dashboard/ClientApp/
├── package.json
├── package-lock.json
├── tsconfig.json
└── src/api/generated/
```

Only `openapi-typescript` and `typescript` are permitted as development dependencies in this slice. Dependency versions are exact and the npm lockfile is committed. TypeScript is configured strictly and emits no JavaScript.

React, Vite, Axios, query libraries, formatters, chart packages, and behavioral HTTP-client code remain out of scope for FC-029.1C.2.

## Next slices

```text
FC-029.1C.3  generate authoritative TypeScript reporting contract
FC-029.1C.4  typecheck, drift, and determinism conformance
FC-029.1D    typed reporting client boundary
FC-029.1E    React application shell and query state
FC-029.1F    representative HTTP → presentation vertical proof
FC-029.1G    architecture and deployment conformance
```
