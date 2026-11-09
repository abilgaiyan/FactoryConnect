-- PR #180-B: read-only MOXA source/publication eligibility probe.
-- No fixture admission, mutation, or qualification is performed.
SET NOCOUNT ON;
IF @@SERVERNAME <> N'DESKTOP-1PJK2OB\FCINVENTORY'
    THROW 51000, 'Unexpected SQL instance; abort.', 1;
IF DB_NAME() <> N'FactoryConnect_P0_GateB_20261109'
    THROW 51001, 'Unexpected disposable Gate B database; abort.', 1;

DECLARE @Machine uniqueidentifier = 'bfa0dc97-9a68-45ed-b189-1dacb7d863f0';
-- Persisted stream keys use big-endian UTF-16 (OrdinalStringKeyCodec), not SQL Server's little-endian nvarchar conversion.
DECLARE @Stream varbinary(512) =
  0x006D00740063006F006E006E006500630074003A00440045005700410053002D00530052002D004D004F00580041002D003000300031;

SELECT 'acquisition-checkpoint' AS Evidence, MachineId, InstanceId, NextSequence
FROM dbo.ObservationStreamCheckpoint
WHERE MachineId=@Machine AND StreamKeyBinary=@Stream;

SELECT 'publication-head' AS Evidence, h.MachineId, p.PublicationOrdinal,
       CONVERT(varchar(64),p.IdentityHash,2) AS IdentityHash,
       p.AcceptedBefore,p.AcceptedThrough,p.PayloadVersion
FROM dbo.AcquisitionEvidenceHead AS h
LEFT JOIN dbo.AcquisitionEvidencePublication AS p
 ON p.MachineId=h.MachineId AND p.StreamKeyBinary=h.StreamKeyBinary AND p.IdentityHash=h.IdentityHash
WHERE h.MachineId=@Machine AND h.StreamKeyBinary=@Stream;

SELECT TOP (5) 'publication-tail' AS Evidence, PublicationOrdinal,
       CONVERT(varchar(64),IdentityHash,2) AS IdentityHash,
       CONVERT(varchar(64),PreviousIdentity,2) AS PreviousIdentity,
       AcceptedBefore,AcceptedThrough,PayloadVersion
FROM dbo.AcquisitionEvidencePublication
WHERE MachineId=@Machine AND StreamKeyBinary=@Stream
ORDER BY PublicationOrdinal DESC;

SELECT 'activity-authority' AS Evidence, MachineId, Position, ProjectionRevision, ActiveStartedAt, MachineState
FROM dbo.MachineStateActivityAuthority
WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND StateProcessorId=N'machine-state-activity';

SELECT TOP (12) 'status-history' AS Evidence, Position, InstanceId, Sequence, Address,
       ObservationValue, Quality, ObservedAt
FROM dbo.MachineObservation
WHERE MachineId=@Machine AND StreamKeyBinary=@Stream AND Address=N'moxa001_status'
ORDER BY Position DESC;

SELECT 'raw-frontier' AS Evidence, COUNT_BIG(*) AS RetainedRows, MAX(Position) AS LastPosition
FROM dbo.MachineObservation WHERE MachineId=@Machine AND StreamKeyBinary=@Stream;
