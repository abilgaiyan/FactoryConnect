ALTER TABLE dbo.MachineObservation
ADD Position decimal(20,0) NULL;

EXEC sys.sp_executesql N'
WITH RankedObservation AS
(
    SELECT
        Position,
        ROW_NUMBER() OVER
        (
            PARTITION BY MachineId, StreamKeyBinary
            ORDER BY InstanceId, Sequence
        ) AS AssignedPosition
    FROM dbo.MachineObservation
)
UPDATE RankedObservation
SET Position = CONVERT(decimal(20,0), AssignedPosition);

IF EXISTS
(
    SELECT 1
    FROM dbo.MachineObservation
    WHERE Position IS NULL
       OR Position < 1
       OR Position > 18446744073709551615
)
    THROW 51000, ''Migration 007 could not establish valid observation positions.'', 1;

IF EXISTS
(
    SELECT 1
    FROM dbo.MachineObservation
    GROUP BY MachineId, StreamKeyBinary, Position
    HAVING COUNT_BIG(*) > 1
)
    THROW 51001, ''Migration 007 produced duplicate observation positions.'', 1;

ALTER TABLE dbo.MachineObservation
ALTER COLUMN Position decimal(20,0) NOT NULL;

ALTER TABLE dbo.MachineObservation
ADD CONSTRAINT CK_MachineObservation_Position_UInt64Positive
    CHECK (Position BETWEEN 1 AND 18446744073709551615);

ALTER TABLE dbo.MachineObservation
ADD CONSTRAINT UQ_MachineObservation_StreamPosition
    UNIQUE NONCLUSTERED (MachineId, StreamKeyBinary, Position);
';

CREATE TABLE dbo.AcquisitionContactAuthority
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    SuccessfulContactTime datetimeoffset(7) NOT NULL,
    RawAcceptedThrough decimal(20,0) NULL,
    AcquisitionRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_AcquisitionContactAuthority
        PRIMARY KEY (MachineId, StreamKeyBinary),

    CONSTRAINT FK_AcquisitionContactAuthority_Checkpoint
        FOREIGN KEY (MachineId, StreamKeyBinary)
        REFERENCES dbo.ObservationStreamCheckpoint
            (MachineId, StreamKeyBinary),

    CONSTRAINT FK_AcquisitionContactAuthority_RawAcceptedThrough
        FOREIGN KEY (MachineId, StreamKeyBinary, RawAcceptedThrough)
        REFERENCES dbo.MachineObservation
            (MachineId, StreamKeyBinary, Position),

    CONSTRAINT CK_AcquisitionContactAuthority_SuccessfulContactTimeUtc
        CHECK (DATEPART(TZOFFSET, SuccessfulContactTime) = 0),

    CONSTRAINT CK_AcquisitionContactAuthority_RawAcceptedThrough_UInt64Positive
        CHECK (
            RawAcceptedThrough IS NULL
            OR RawAcceptedThrough BETWEEN 1 AND 18446744073709551615
        ),

    CONSTRAINT CK_AcquisitionContactAuthority_AcquisitionRevision_UInt64
        CHECK (
            AcquisitionRevision BETWEEN 0 AND 18446744073709551615
        )
);
