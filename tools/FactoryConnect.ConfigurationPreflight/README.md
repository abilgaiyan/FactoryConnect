# GG-003 G2 configuration preflight

Development-only configuration inspection. This executable never builds/starts the
Edge host, registers SQL persistence or acquisition, or invokes hosted services.
It binds the existing production metric input registration and resolves schedules
through the existing domain readers. No production configuration is embedded here.

From the repository root on a development machine with the repository's SDK:

```powershell
dotnet test tests/FactoryConnect.Edge.Tests/FactoryConnect.Edge.Tests.csproj -c Release --filter FullyQualifiedName~G2ConfigurationPreflightTests
dotnet run --project tools/FactoryConnect.ConfigurationPreflight -c Release -- "F:\G2\edge.g2.candidate.json" "F:\G2\captured-edge-release" "<approved-business-date>"
```

The second argument must contain the matching release's `appsettings.json` and any
`appsettings.Production.json`. Alternatively use `src/FactoryConnect.Edge` to test
source defaults, recording that distinction in the evidence. Run in a development
shell representing the intended child environment. The tool retains inherited
variables as the launcher does; additional inherited assignment keys cause failure.
The probe date is a test input, not approval of an effective date. Eight complete
business dates are checked to cover every weekday and an adjacent-day boundary.

Projection uses the launcher's object/array `__` keys, lowercase booleans and empty
strings for null. Projected keys and both Production selectors are installed before
`Host.CreateApplicationBuilder` reads its default providers. The application name
is the Edge assembly, content root is the supplied release directory, and host
arguments are empty. Environment is restored on success and failure. Unit tests
that use this API run in a collection with parallelization disabled.

`UNRESOLVED:` values produce `BLOCKED` and exit 2 before binding. Binding failures
exit 1. Successful binding exits 0 with `BOUND_NOT_DEPLOYMENT_APPROVED`; this does
not approve dates, breaks, database targets, historical correction, or deployment.
Reports include only assignment identities, timezone, UTC shift intervals and
computed minutes, not connection strings or arbitrary configuration values.

The private candidate retains the captured machine/scope and existing shift IDs.
New context/planned IDs must be explicitly chosen; historical identities must not
be inferred from former demonstration defaults. Do not commit captured production
files or connection details. Resolve business inputs and review the resulting
candidate diff before running the binder; unresolved candidate tests are not
successful effective-host validation.

Limits: this is the production-processing configuration slice, not a full Edge
startup/readiness test. It does not validate SQL connectivity/migrations, roster
persistence, quantity facts, metric publication or database cutover. Existing core,
integration and SQL suites remain separate gates. It resolves only the supplied
candidate and does not reconstruct an old/new historical schedule transition.
