-- PR #180-B: READ-ONLY preflight for a NEW disposable Gate B clone.
-- Execute only in a query window explicitly connected to the named clone.
-- This script does not authorize acquisition, fixture admission or production deployment.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
-- Require the operator to create this new database by restoring the approved backup.
-- Never use the preserved incident or existing qualification databases.
DECLARE @ExpectedServer sysname = N'DESKTOP-1PJK2OB\FCINVENTORY';
DECLARE @ExpectedDatabase sysname = N'FactoryConnect_P0_GateB_Fixture_20261110';
IF @@SERVERNAME <> @ExpectedServer
    THROW 51100, 'Gate B preflight refused: unexpected SQL Server.', 1;
IF DB_NAME() <> @ExpectedDatabase
    THROW 51101, 'Gate B preflight refused: expected NEW disposable clone.', 1;
IF DB_NAME() IN (N'FactoryConnect', N'FactoryConnect_P0_20261105',
                 N'FactoryConnect_P0_Qualification_20261106',
                 N'FactoryConnect_P0_GateB_20261109')
    THROW 51102, 'Gate B preflight refused: protected database.', 1;
IF DATABASEPROPERTYEX(DB_NAME(), 'Updateability') <> 'READ_WRITE'
    THROW 51103, 'Gate B preflight refused: clone is not writable.', 1;

DECLARE @Gg uniqueidentifier = '677b030b-1ede-4a67-82ae-6a494791357f';
DECLARE @Moxa uniqueidentifier = 'bfa0dc97-9a68-45ed-b189-1dacb7d863f0';
DECLARE @MoxaStream varbinary(512) =
  0x006D00740063006F006E006E006500630074003A00440045005700410053002D00530052002D004D004F00580041002D003000300031;
-- Confirm this clone has the expected preserved acquisition and activity frontier.
-- The publication identity hash is deliberately reported, not compared to an
-- unverified constant. Independently verify the entire cryptographic chain
-- with the production evidence reader before authorizing any fixture writes.
IF NOT EXISTS (
    SELECT 1 FROM dbo.ObservationStreamCheckpoint
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream
      AND InstanceId = 1793867780 AND NextSequence = 27)
    THROW 51104, 'Gate B preflight refused: MOXA acquisition frontier differs.', 1;
IF NOT EXISTS (
    SELECT 1 FROM dbo.MachineStateActivityAuthority
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream
      AND StateProcessorId = N'machine-state-activity'
      AND Position = 14324 AND ProjectionRevision = 4703)
    THROW 51105, 'Gate B preflight refused: MOXA activity frontier differs.', 1;
IF NOT EXISTS (
    SELECT 1 FROM dbo.AcquisitionEvidenceHead AS h
    JOIN dbo.AcquisitionEvidencePublication AS p
      ON p.MachineId = h.MachineId
     AND p.StreamKeyBinary = h.StreamKeyBinary
     AND p.IdentityHash = h.IdentityHash
    WHERE h.MachineId = @Moxa AND h.StreamKeyBinary = @MoxaStream
      AND p.PublicationOrdinal = 104153
      AND p.AcceptedBefore = 14328 AND p.AcceptedThrough = 14328
      AND p.PayloadVersion = 1)
    THROW 51106, 'Gate B preflight refused: MOXA publication head differs.', 1;
IF NOT EXISTS (
    SELECT 1 FROM dbo.MachineStateActivityAuthority
    WHERE MachineId = @Gg AND StateProcessorId = N'machine-state-activity'
      AND Position = 513501 AND ProjectionRevision = 4091)
    THROW 51107, 'Gate B preflight refused: GG chronology hold baseline differs.', 1;

-- Structural checks only. They do not validate the canonical publication payload
-- or cryptographic identity chain; the production reader must do that separately.
IF (SELECT COUNT_BIG(*) FROM dbo.AcquisitionEvidenceHead
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream) <> 1
    THROW 51108, 'Gate B preflight refused: MOXA head missing or ambiguous.', 1;
IF (SELECT COUNT_BIG(*) FROM dbo.AcquisitionEvidencePublication
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream
      AND PublicationOrdinal = 104153) <> 1
    THROW 51109, 'Gate B preflight refused: MOXA tail ordinal missing or ambiguous.', 1;
IF EXISTS (
    SELECT 1 FROM dbo.AcquisitionEvidencePublication
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream
      AND PublicationOrdinal > 104153)
    THROW 51110, 'Gate B preflight refused: MOXA publication frontier has moved.', 1;
IF NOT EXISTS (
    SELECT 1 FROM dbo.MachineObservation
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream
      AND Position = 14328)
    THROW 51111, 'Gate B preflight refused: MOXA retained frontier is absent.', 1;
IF EXISTS (
    SELECT 1 FROM dbo.MachineObservation
    WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream
      AND Position > 14328)
    THROW 51112, 'Gate B preflight refused: MOXA raw frontier has moved.', 1;

SELECT DB_NAME() AS VerifiedClone, @@SERVERNAME AS VerifiedServer,
       N'READ_ONLY_BASELINE_MATCH; NOT AN ADMISSION AUTHORIZATION' AS Disposition;
SELECT MachineId, InstanceId, NextSequence
FROM dbo.ObservationStreamCheckpoint
WHERE MachineId = @Moxa AND StreamKeyBinary = @MoxaStream;
SELECT MachineId, Position, ProjectionRevision, MachineState, ActiveStartedAt
FROM dbo.MachineStateActivityAuthority WHERE MachineId IN (@Gg, @Moxa);
SELECT h.MachineId, p.PublicationOrdinal, p.AcceptedBefore, p.AcceptedThrough,
       CONVERT(varchar(64), p.IdentityHash, 2) AS IdentityHash
FROM dbo.AcquisitionEvidenceHead h
JOIN dbo.AcquisitionEvidencePublication p
  ON p.MachineId = h.MachineId AND p.StreamKeyBinary = h.StreamKeyBinary
 AND p.IdentityHash = h.IdentityHash
WHERE h.MachineId = @Moxa AND h.StreamKeyBinary = @MoxaStream;
