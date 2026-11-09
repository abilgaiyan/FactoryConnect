-- PR #180-B / read-only MOXA fixture preflight.
-- Run ONLY against the local disposable qualification baseline.
-- Does not create, update, delete or reset any evidence.
SET NOCOUNT ON;
IF @@SERVERNAME <> N'DESKTOP-1PJK2OB\FCINVENTORY'
    THROW 51000, 'Unexpected SQL instance; abort.', 1;
IF DB_NAME() <> N'FactoryConnect_P0_Qualification_20261106'
    THROW 51001, 'Unexpected database; abort.', 1;

DECLARE @MachineId uniqueidentifier = 'bfa0dc97-9a68-45ed-b189-1dacb7d863f0';
DECLARE @Stream varbinary(512) =
  0x006D00740063006F006E006E006500630074003A00440045005700410053002D00530052002D004D004F00580041002D003000300031;

SELECT 'acquisition-checkpoint' AS Evidence, MachineId, StreamKeyBinary, InstanceId, NextSequence
FROM dbo.ObservationStreamCheckpoint
WHERE MachineId=@MachineId AND StreamKeyBinary=@Stream;

SELECT 'raw-frontier' AS Evidence, COUNT_BIG(*) AS RetainedRows,
       MIN(Position) AS FirstPosition, MAX(Position) AS LastPosition
FROM dbo.MachineObservation
WHERE MachineId=@MachineId AND StreamKeyBinary=@Stream;

SELECT TOP (12) 'last-raw' AS Evidence, Position, InstanceId, Sequence,
       Source, Address, SignalType, ObservationValue, Quality, ObservedAt
FROM dbo.MachineObservation
WHERE MachineId=@MachineId AND StreamKeyBinary=@Stream
ORDER BY Position DESC;

SELECT 'activity-authority' AS Evidence, MachineId, StateProcessorId, Position,
       ProjectionRevision, ActiveStartedAt
FROM dbo.MachineStateActivityAuthority
WHERE MachineId=@MachineId AND StreamKeyBinary=@Stream
  AND StateProcessorId=N'machine-state-activity';

SELECT 'processing-checkpoint' AS Evidence, ProcessorId, Position
FROM dbo.ObservationProcessingCheckpoint
WHERE MachineId=@MachineId AND StreamKeyBinary=@Stream
ORDER BY ProcessorId;

-- No mapping/configuration tables queried until their schema and exact
-- commissioned status-value contract have been source-verified.
