# PR #180 — Gate B production-code publication verification

This is a **read-only** executable that invokes the actual `SqlServerAcquisitionEvidenceReader.ReadExactAsync` against the expected MOXA publication head in the **disposable** SQL clone. The production reader validates the entire retained prefix through ordinal 104153, including canonical payload encoding, identity hashes, envelope content hashes, predecessor links, checkpoint/accepted boundaries and persisted observation source equivalence. This is not a replacement SQL-only approximation.

Run on the Windows development machine from the PR #180 checkout, after the GateB-DisposablePreflight.sql returns READ_ONLY_BASELINE_MATCH:

```powershell
$env:FC_GATEB_SQL_CONNECTION = 'Server=DESKTOP-1PJK2OB\FCINVENTORY;Database=FactoryConnect_P0_GateB_Fixture_20261110;Integrated Security=True;TrustServerCertificate=True'
dotnet run --project tools/FactoryConnect.GateBEvidenceVerifier -- --verify
$exitCode = $LASTEXITCODE
Remove-Item Env:FC_GATEB_SQL_CONNECTION
if ($exitCode -ne 0) { throw "Gate B publication verification blocked ($exitCode)" }
```

Expected: `PASS: verified production evidence prefix through ordinal 104153`. Exit 1 means BLOCKED (including corrupt/absent/unsupported chain or mismatched frontier); exit 2 means invalid invocation. Do not infer PASS from a timeout or from the SQL structural preflight alone.

The production verifier may take significant time on the full historical prefix. Capture UTC start/end and stdout/stderr. Never alter the clone to force PASS. This tool never commits evidence or starts acquisition, and it does **not** qualify MOXA activity advancement, replay, host restart, or production deployment.
