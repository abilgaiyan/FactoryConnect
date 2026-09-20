CREATE TABLE dbo.ObservationProcessingCheckpoint
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    ProcessorId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProcessorIdOrderKey varbinary(769) NOT NULL,
    Position decimal(20,0) NOT NULL,

    CONSTRAINT PK_ObservationProcessingCheckpoint
        PRIMARY KEY CLUSTERED
        (MachineId, StreamKeyBinary, ProcessorIdOrderKey),

    CONSTRAINT FK_ObservationProcessingCheckpoint_Stream
        FOREIGN KEY (MachineId, StreamKeyBinary)
        REFERENCES dbo.ObservationStreamCheckpoint
            (MachineId, StreamKeyBinary),

    CONSTRAINT FK_ObservationProcessingCheckpoint_Position
        FOREIGN KEY (MachineId, StreamKeyBinary, Position)
        REFERENCES dbo.MachineObservation
            (MachineId, StreamKeyBinary, Position),

    CONSTRAINT CK_ObservationProcessingCheckpoint_ProcessorId
        CHECK (DATALENGTH(ProcessorId) > 0),

    CONSTRAINT CK_ObservationProcessingCheckpoint_ProcessorOrderKey
        CHECK (DATALENGTH(ProcessorIdOrderKey) BETWEEN 1 AND 769),

    CONSTRAINT CK_ObservationProcessingCheckpoint_Position_UInt64Positive
        CHECK (Position BETWEEN 1 AND 18446744073709551615)
);
