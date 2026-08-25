CREATE TABLE dbo.ObservationStreamCheckpoint
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    StreamKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    InstanceId decimal(20,0) NOT NULL,
    NextSequence decimal(20,0) NOT NULL,

    CONSTRAINT PK_ObservationStreamCheckpoint
        PRIMARY KEY (MachineId, StreamKeyBinary),

    CONSTRAINT CK_ObservationStreamCheckpoint_InstanceId_UInt64
        CHECK (
            InstanceId >= 0
            AND InstanceId <= 18446744073709551615
        ),

    CONSTRAINT CK_ObservationStreamCheckpoint_NextSequence_UInt64
        CHECK (
            NextSequence >= 0
            AND NextSequence <= 18446744073709551615
        )
);

CREATE TABLE dbo.MachineObservation
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    InstanceId decimal(20,0) NOT NULL,
    Sequence decimal(20,0) NOT NULL,
    Source nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Address nvarchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SignalType tinyint NOT NULL,
    ObservationValue nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
    Quality tinyint NOT NULL,
    ObservedAt datetimeoffset(7) NOT NULL,

    CONSTRAINT PK_MachineObservation
        PRIMARY KEY (
            MachineId,
            StreamKeyBinary,
            InstanceId,
            Sequence
        ),

    CONSTRAINT FK_MachineObservation_ObservationStreamCheckpoint
        FOREIGN KEY (MachineId, StreamKeyBinary)
        REFERENCES dbo.ObservationStreamCheckpoint (
            MachineId,
            StreamKeyBinary
        ),

    CONSTRAINT CK_MachineObservation_InstanceId_UInt64
        CHECK (
            InstanceId >= 0
            AND InstanceId <= 18446744073709551615
        ),

    CONSTRAINT CK_MachineObservation_Sequence_UInt64
        CHECK (
            Sequence >= 0
            AND Sequence <= 18446744073709551615
        ),

    CONSTRAINT CK_MachineObservation_SignalType
        CHECK (SignalType BETWEEN 0 AND 7),

    CONSTRAINT CK_MachineObservation_Quality
        CHECK (Quality BETWEEN 0 AND 2)
);
