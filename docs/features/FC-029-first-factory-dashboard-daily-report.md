# FC-029 — First Factory Dashboard / Daily Report

## Status

- **FC-029.1A — merged FC-028 contract inventory:** complete
- **FC-029.1B — dashboard host foundation:** active

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

React, Node, OpenAPI generation, chart packages, reporting HTTP traffic, and presentation view models are deliberately deferred.

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

## Next slices

```text
FC-029.1C  OpenAPI → TypeScript generation
FC-029.1D  typed reporting client boundary
FC-029.1E  React application shell and query state
FC-029.1F  representative HTTP → presentation vertical proof
FC-029.1G  architecture and deployment conformance
```
