# GG-003 A2 real-history append/replay benchmark

This is an opt-in test against an **existing disposable LocalDB clone**. It neither
creates nor migrates a fixture. Run A2 first. A1 is deliberately not accepted by this
harness. No protected-reference or production database is allowed.

Run only while Edge/API and other writers/readers are disconnected from this clone.
The baseline is exactly 40,545 publications, instance 1792061301, next sequence 482204.
The expected head hash, accepted-through position, observation count and exact stream
key must come from the preserved baseline. Do not substitute values from an already
advanced clone merely to make a rerun pass.

## Read-only preflight in SSMS

```sql
USE [GG003_ACQ_A2_20261026];
IF DB_NAME() <> N'GG003_ACQ_A2_20261026'
    THROW 51090, 'Unexpected benchmark database', 1;
SELECT c.StreamKey, c.InstanceId, c.NextSequence,
    CONVERT(varchar(64),p.IdentityHash,2) AS ExpectedHead,
    p.PublicationOrdinal, p.AcceptedThrough,
    (SELECT COUNT_BIG(*) FROM dbo.MachineObservation o
     WHERE o.MachineId=c.MachineId AND o.StreamKeyBinary=c.StreamKeyBinary) AS ObservationCount
FROM dbo.ObservationStreamCheckpoint c
JOIN dbo.AcquisitionEvidenceHead h ON h.MachineId=c.MachineId AND h.StreamKeyBinary=c.StreamKeyBinary
JOIN dbo.AcquisitionEvidencePublication p ON p.MachineId=h.MachineId
    AND p.StreamKeyBinary=h.StreamKeyBinary AND p.IdentityHash=h.IdentityHash
WHERE c.MachineId='677b030b-1ede-4a67-82ae-6a494791357f';
```

## Execute once from the benchmark revision in the A2 worktree

The connection must target `(localdb)\MSSQLLocalDB` (or another developer LocalDB
instance) and exactly `GG003_ACQ_A2_20261026`. The separate acknowledgement must match.
Fill the three baseline values and stream key below from the read-only preflight:

```powershell
$env:FACTORYCONNECT_GG003_ACQ_A2_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=GG003_ACQ_A2_20261026;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:FACTORYCONNECT_GG003_ACQ_A2_DISPOSABLE_DATABASE = 'GG003_ACQ_A2_20261026'
$env:FACTORYCONNECT_GG003_ACQ_A2_STREAM_KEY = '<exact StreamKey>'
$env:FACTORYCONNECT_GG003_ACQ_A2_EXPECTED_HEAD = '<64 hex characters>'
$env:FACTORYCONNECT_GG003_ACQ_A2_EXPECTED_ACCEPTED_THROUGH = '<AcceptedThrough>'
$env:FACTORYCONNECT_GG003_ACQ_A2_EXPECTED_OBSERVATION_COUNT = '<ObservationCount>'
$env:FACTORYCONNECT_GG003_ACQ_A2_TIMEOUT_SECONDS = '600'
dotnet restore tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Gg003AcquisitionCloneBenchmarkTests' --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=gg003-acquisition-A2-clone.trx' --results-directory artifacts/TestResults/GG003-A2-clone
```

Do not use `--no-build` on the first run. The test skips when the connection variable
is absent; an explicit but invalid configuration fails. The total cooperative deadline
defaults to 600 seconds (allowed 30–1800), independently of normal SqlClient command
timeouts. Cancellation can take additional time to unwind and roll back. No factory
timeout configuration is changed.

The test checks the actual SQL database, trusted/enabled enum CHECK constraints,
exact baseline head, counts, accepted-through and checkpoint, then validates the full
historical head using the real reader. It constructs a clearly labeled synthetic
Text observation at sequence 482204, a checkpoint of 482205, and response evidence
requested/first/last=482204 with no recoveries. The timestamp is one second after the
prior successful contact. The actual codec validates the batch before append.

It records exact-read, append and replay wall times, attempted source SQL commands
and every acquisition stage span, including incomplete stages on failure. It verifies
one new observation/publication and position, the exact head/previous identity,
checkpoint/contact, a new store instance reading the appended evidence and observation,
then replays the identical in-memory batch/GUID and asserts unchanged state. On append
failure it checks rollback with a fresh 30-second bound. State comparison covers the
checkpoint, evidence head, publication/observation counts and contact-authority content;
it is not a byte-for-byte digest of every historical row.

**A successful run leaves the disposable clone advanced by one publication and one
observation.** No automatic deletion or rewind is attempted. Preserve the TRX and
restore the original clone before repeating this baseline run. A timeout after a
commit may leave the clone advanced even though later assertions did not run; inspect
the head before retrying. Clear the opt-in variables before running unrelated tests.

This first R4 probe measures A2 full-history append/replay only. It does not collect
concurrent-reader latency or SQL wait samples, and transaction lifetime is not direct
X-lock duration. It cannot close the real-history contention gate by itself. Review
its complete result before preparing an equivalent A1 run or a separate controlled
concurrent-reader/wait measurement. Production deployment remains outside this test.
