CREATE TABLE dbo.MappingCoverageAuthority
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    MappingProcessorId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MappingProcessorIdOrderKey varbinary(769) NOT NULL,
    RawConsumedThrough decimal(20,0) NOT NULL,
    MappedEvaluationInputHighWater decimal(20,0) NULL,
    MappingRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_MappingCoverageAuthority
        PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary, MappingProcessorIdOrderKey),

    CONSTRAINT FK_MappingCoverageAuthority_Checkpoint
        FOREIGN KEY (MachineId, StreamKeyBinary)
        REFERENCES dbo.ObservationStreamCheckpoint (MachineId, StreamKeyBinary),

    CONSTRAINT CK_MappingCoverageAuthority_ProcessorId
        CHECK (DATALENGTH(MappingProcessorId) > 0),

    CONSTRAINT CK_MappingCoverageAuthority_ProcessorOrderKey
        CHECK (DATALENGTH(MappingProcessorIdOrderKey) BETWEEN 1 AND 769),

    CONSTRAINT CK_MappingCoverageAuthority_RawConsumedThrough_UInt64Positive
        CHECK (RawConsumedThrough BETWEEN 1 AND 18446744073709551615),

    CONSTRAINT CK_MappingCoverageAuthority_MappedHighWater_UInt64Positive
        CHECK (MappedEvaluationInputHighWater IS NULL OR MappedEvaluationInputHighWater BETWEEN 1 AND 18446744073709551615),

    CONSTRAINT CK_MappingCoverageAuthority_HighWater
        CHECK (MappedEvaluationInputHighWater IS NULL OR MappedEvaluationInputHighWater <= RawConsumedThrough),

    CONSTRAINT CK_MappingCoverageAuthority_MappingRevision_UInt64
        CHECK (MappingRevision BETWEEN 0 AND 18446744073709551615)
);

CREATE TABLE dbo.MachineStateActivityAuthority
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    StateProcessorId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StateProcessorIdOrderKey varbinary(769) NOT NULL,
    Position decimal(20,0) NOT NULL,
    MachineState tinyint NOT NULL,
    ActiveState tinyint NULL,
    ActiveStartedAt datetimeoffset(7) NULL,
    LastConsumedInstanceId decimal(20,0) NOT NULL,
    ContinuityPolicyIdentity nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ContinuityPolicyVersion nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProjectionRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_MachineStateActivityAuthority
        PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary, StateProcessorIdOrderKey),

    CONSTRAINT FK_MachineStateActivityAuthority_Checkpoint
        FOREIGN KEY (MachineId, StreamKeyBinary)
        REFERENCES dbo.ObservationStreamCheckpoint (MachineId, StreamKeyBinary),

    CONSTRAINT CK_MachineStateActivityAuthority_ProcessorId
        CHECK (DATALENGTH(StateProcessorId) > 0),

    CONSTRAINT CK_MachineStateActivityAuthority_ProcessorOrderKey
        CHECK (DATALENGTH(StateProcessorIdOrderKey) BETWEEN 1 AND 769),

    CONSTRAINT CK_MachineStateActivityAuthority_Position_UInt64Positive
        CHECK (Position BETWEEN 1 AND 18446744073709551615),

    CONSTRAINT CK_MachineStateActivityAuthority_MachineState
        CHECK (MachineState BETWEEN 0 AND 4),

    CONSTRAINT CK_MachineStateActivityAuthority_ActiveState
        CHECK (ActiveState IS NULL OR ActiveState BETWEEN 0 AND 4),

    CONSTRAINT CK_MachineStateActivityAuthority_ActivePair
        CHECK ((ActiveState IS NULL AND ActiveStartedAt IS NULL) OR (ActiveState IS NOT NULL AND ActiveStartedAt IS NOT NULL)),

    CONSTRAINT CK_MachineStateActivityAuthority_LastConsumedInstanceId_UInt64
        CHECK (LastConsumedInstanceId BETWEEN 0 AND 18446744073709551615),

    CONSTRAINT CK_MachineStateActivityAuthority_ContinuityPolicyIdentity
        CHECK (DATALENGTH(ContinuityPolicyIdentity) > 0),

    CONSTRAINT CK_MachineStateActivityAuthority_ContinuityPolicyVersion
        CHECK (DATALENGTH(ContinuityPolicyVersion) > 0),

    CONSTRAINT CK_MachineStateActivityAuthority_ProjectionRevision_UInt64
        CHECK (ProjectionRevision BETWEEN 0 AND 18446744073709551615)
);

CREATE TABLE dbo.MachineStateActivitySignal
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    StateProcessorIdOrderKey varbinary(769) NOT NULL,
    SignalOrdinal int NOT NULL,
    SignalKey nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SignalType tinyint NOT NULL,
    DigitalValue bit NULL,
    DecimalValue nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    CounterValue decimal(20,0) NULL,
    WholeNumberValue bigint NULL,
    TextValue nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
    TimestampValue datetimeoffset(7) NULL,
    Source nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
    Quality tinyint NOT NULL,
    Timestamp datetimeoffset(7) NOT NULL,

    CONSTRAINT PK_MachineStateActivitySignal
        PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, SignalOrdinal),

    CONSTRAINT FK_MachineStateActivitySignal_Authority
        FOREIGN KEY (MachineId, StreamKeyBinary, StateProcessorIdOrderKey)
        REFERENCES dbo.MachineStateActivityAuthority (MachineId, StreamKeyBinary, StateProcessorIdOrderKey),

    CONSTRAINT CK_MachineStateActivitySignal_Ordinal CHECK (SignalOrdinal >= 0),
    CONSTRAINT CK_MachineStateActivitySignal_SignalKey CHECK (DATALENGTH(SignalKey) > 0),
    CONSTRAINT CK_MachineStateActivitySignal_SignalType CHECK (SignalType BETWEEN 0 AND 7),
    CONSTRAINT CK_MachineStateActivitySignal_Quality CHECK (Quality BETWEEN 0 AND 2),
    CONSTRAINT CK_MachineStateActivitySignal_CounterValue_UInt64 CHECK (CounterValue IS NULL OR CounterValue BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_MachineStateActivitySignal_ValueShape CHECK
    (
        (DigitalValue IS NULL AND DecimalValue IS NULL AND CounterValue IS NULL AND WholeNumberValue IS NULL AND TextValue IS NULL AND TimestampValue IS NULL)
        OR (SignalType = 0 AND DigitalValue IS NOT NULL AND DecimalValue IS NULL AND CounterValue IS NULL AND WholeNumberValue IS NULL AND TextValue IS NULL AND TimestampValue IS NULL)
        OR (SignalType IN (1,4) AND DigitalValue IS NULL AND DecimalValue IS NOT NULL AND CounterValue IS NULL AND WholeNumberValue IS NULL AND TextValue IS NULL AND TimestampValue IS NULL)
        OR (SignalType = 2 AND DigitalValue IS NULL AND DecimalValue IS NULL AND CounterValue IS NOT NULL AND WholeNumberValue IS NULL AND TextValue IS NULL AND TimestampValue IS NULL)
        OR (SignalType = 3 AND DigitalValue IS NULL AND DecimalValue IS NULL AND CounterValue IS NULL AND WholeNumberValue IS NOT NULL AND TextValue IS NULL AND TimestampValue IS NULL)
        OR (SignalType IN (5,6) AND DigitalValue IS NULL AND DecimalValue IS NULL AND CounterValue IS NULL AND WholeNumberValue IS NULL AND TextValue IS NOT NULL AND TimestampValue IS NULL)
        OR (SignalType = 7 AND DigitalValue IS NULL AND DecimalValue IS NULL AND CounterValue IS NULL AND WholeNumberValue IS NULL AND TextValue IS NULL AND TimestampValue IS NOT NULL)
    )
);

CREATE TABLE dbo.MachineStateChangeHistory
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    StateProcessorIdOrderKey varbinary(769) NOT NULL,
    ProjectionRevision decimal(20,0) NOT NULL,
    OutputOrdinal int NOT NULL,
    Position decimal(20,0) NOT NULL,
    PreviousState tinyint NOT NULL,
    CurrentState tinyint NOT NULL,
    OccurredAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MachineStateChangeHistory PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision, OutputOrdinal),
    CONSTRAINT FK_MachineStateChangeHistory_Authority FOREIGN KEY (MachineId, StreamKeyBinary, StateProcessorIdOrderKey) REFERENCES dbo.MachineStateActivityAuthority (MachineId, StreamKeyBinary, StateProcessorIdOrderKey),
    CONSTRAINT CK_MachineStateChangeHistory_ProjectionRevision_UInt64 CHECK (ProjectionRevision BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_MachineStateChangeHistory_OutputOrdinal CHECK (OutputOrdinal >= 0),
    CONSTRAINT CK_MachineStateChangeHistory_Position_UInt64Positive CHECK (Position BETWEEN 1 AND 18446744073709551615),
    CONSTRAINT CK_MachineStateChangeHistory_PreviousState CHECK (PreviousState BETWEEN 0 AND 4),
    CONSTRAINT CK_MachineStateChangeHistory_CurrentState CHECK (CurrentState BETWEEN 0 AND 4)
);

CREATE TABLE dbo.MachineActivityPeriodHistory
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    StateProcessorIdOrderKey varbinary(769) NOT NULL,
    ProjectionRevision decimal(20,0) NOT NULL,
    OutputOrdinal int NOT NULL,
    Position decimal(20,0) NOT NULL,
    MachineState tinyint NOT NULL,
    StartedAt datetimeoffset(7) NOT NULL,
    EndedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MachineActivityPeriodHistory PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary, StateProcessorIdOrderKey, ProjectionRevision, OutputOrdinal),
    CONSTRAINT FK_MachineActivityPeriodHistory_Authority FOREIGN KEY (MachineId, StreamKeyBinary, StateProcessorIdOrderKey) REFERENCES dbo.MachineStateActivityAuthority (MachineId, StreamKeyBinary, StateProcessorIdOrderKey),
    CONSTRAINT CK_MachineActivityPeriodHistory_ProjectionRevision_UInt64 CHECK (ProjectionRevision BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_MachineActivityPeriodHistory_OutputOrdinal CHECK (OutputOrdinal >= 0),
    CONSTRAINT CK_MachineActivityPeriodHistory_Position_UInt64Positive CHECK (Position BETWEEN 1 AND 18446744073709551615),
    CONSTRAINT CK_MachineActivityPeriodHistory_MachineState CHECK (MachineState BETWEEN 0 AND 4),
    CONSTRAINT CK_MachineActivityPeriodHistory_Interval CHECK (EndedAt >= StartedAt)
);

CREATE NONCLUSTERED INDEX IX_MachineActivityPeriodHistory_StreamPosition
ON dbo.MachineActivityPeriodHistory
(MachineId ASC, StreamKeyBinary ASC, Position ASC, StateProcessorIdOrderKey ASC, ProjectionRevision ASC, OutputOrdinal ASC)
INCLUDE (MachineState, StartedAt, EndedAt);
