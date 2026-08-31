# FactoryConnect Dashboard — Factory LAN Deployment

## Purpose

This document defines the FC-029.1 production-LAN deployment boundary for `FactoryConnect.Dashboard`.

The dashboard is a presentation host. It serves the React production assets, exposes browser-safe runtime configuration, and forwards only the two FC-028 reporting operations through a same-origin gateway. It does not read FactoryConnect persistence directly and does not calculate factory metrics.

```text
browser
  ↓ same origin
FactoryConnect.Dashboard
  ├── /dashboard/config
  ├── static React assets
  └── exact FC-028 gateway routes
          ↓
FactoryConnect.Api
```

## Clean publish

Publish the dashboard from a clean source tree:

```powershell
dotnet publish src\FactoryConnect.Dashboard\FactoryConnect.Dashboard.csproj -c Release -o <publish-directory>
```

The publish must contain:

```text
wwwroot/index.html
wwwroot/assets/index-<hash>.js
```

The frontend is generated during publish. `src/FactoryConnect.Dashboard/wwwroot/` is generated output and is not a source-of-truth artifact.

## Production host binding

Publishing files does not by itself make the dashboard reachable from another factory-LAN machine. The deployed ASP.NET Core process must bind to a LAN-reachable interface and production environment explicitly.

A minimal environment-variable shape is:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:<dashboard-port>
Dashboard__ReportingApiBaseAddress=http://<reporting-host>:<reporting-port>/<optional-base-path>/
Dashboard__RequestTimeout=00:00:30
Dashboard__Sources__0__MachineId=<machine-guid>
Dashboard__Sources__0__ProcessorId=<processor-id>
Dashboard__Sources__0__DisplayName=<display-name>
```

Additional configured machines continue with `Dashboard__Sources__1__...`, `Dashboard__Sources__2__...`, and so on.

`0.0.0.0` means the Kestrel process listens on all IPv4 interfaces available to that host. Network reachability still depends on the server's IP configuration, routing, and firewall policy. The dashboard port must be exposed only to the intended factory LAN or an equivalently restricted management/network segment; it must not be opened indiscriminately to untrusted networks.

TLS termination, certificate lifecycle, operating-system service supervision, automatic restart, startup ordering, log collection, and machine-level hardening belong to deployment infrastructure. They may be provided by a reverse proxy, Windows Service/systemd/container supervisor, or equivalent site-standard mechanism. FactoryConnect does not silently configure those concerns inside the dashboard application.

## Production configuration

Repository defaults intentionally fail closed:

```json
{
  "Dashboard": {
    "ReportingApiBaseAddress": "",
    "RequestTimeout": "00:00:30",
    "Sources": []
  }
}
```

A production deployment must provide a non-loopback absolute HTTP/HTTPS reporting API address and at least one unique reporting source identity.

The browser never receives `ReportingApiBaseAddress`. `/dashboard/config` exposes only:

```text
reportingBasePath = "/"
requestTimeoutMilliseconds
sources[] = MachineId + ProcessorId + DisplayName
```

## Seven-source pilot composition fixture

The following is a shape example only. Replace every machine identity, processor identity, display name, hostname, and port with the deployed values.

```json
{
  "Dashboard": {
    "ReportingApiBaseAddress": "http://factory-reporting.internal:5080/factoryconnect/",
    "RequestTimeout": "00:00:30",
    "Sources": [
      { "MachineId": "00000000-0000-0000-0000-000000000001", "ProcessorId": "operational-metrics-1", "DisplayName": "Machine 1" },
      { "MachineId": "00000000-0000-0000-0000-000000000002", "ProcessorId": "operational-metrics-2", "DisplayName": "Machine 2" },
      { "MachineId": "00000000-0000-0000-0000-000000000003", "ProcessorId": "operational-metrics-3", "DisplayName": "Machine 3" },
      { "MachineId": "00000000-0000-0000-0000-000000000004", "ProcessorId": "operational-metrics-4", "DisplayName": "Machine 4" },
      { "MachineId": "00000000-0000-0000-0000-000000000005", "ProcessorId": "operational-metrics-5", "DisplayName": "Machine 5" },
      { "MachineId": "00000000-0000-0000-0000-000000000006", "ProcessorId": "operational-metrics-6", "DisplayName": "Machine 6" },
      { "MachineId": "00000000-0000-0000-0000-000000000007", "ProcessorId": "operational-metrics-7", "DisplayName": "Machine 7" }
    ]
  }
}
```

Source identity is exactly `(MachineId, ProcessorId)`. `DisplayName` is presentation metadata only.

## Gateway restriction

The dashboard forwards exactly these two POST operations:

```text
POST /api/reporting/v1/operational-metrics/shifts/query
POST /api/reporting/v1/operational-metrics/production-days/query
```

No generic `/api` proxy exists. Near-miss paths, trailing-slash variants, extra segments, unknown reporting paths, and non-POST methods are not forwarded.

The gateway preserves request JSON bytes and upstream HTTP status/body/content type. It does not retry, cache, reshape, classify, aggregate, or calculate reporting data.

Upstream timeout maps to `504 Gateway Timeout`. Upstream connection/runtime failure maps to `502 Bad Gateway`. Browser cancellation aborts the upstream request.

## Health and startup

```text
GET /health/live
GET /health/ready
```

`live` proves the process is running. `ready` proves the production frontend entry asset exists. Neither endpoint probes FC-028.

Startup validation rejects malformed reporting addresses, loopback production addresses, non-positive or over-five-minute timeouts, empty source sets, empty identities/display names, and duplicate `(MachineId, ProcessorId)` pairs.

## Presentation boundary

The browser may select a production-day business date and submit an FC-028 query. It may format and render returned records. It must not:

- calculate Availability, Performance, Quality, OEE, utilization, or factory-wide percentages;
- infer production-day timezone boundaries;
- infer current machine state;
- combine metric-definition versions;
- reinterpret `unavailable` or `insufficient-evidence` as zero;
- convert failures into empty reporting results;
- inspect or synthesize continuation-token semantics.
