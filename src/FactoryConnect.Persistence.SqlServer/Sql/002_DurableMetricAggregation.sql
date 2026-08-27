CREATE TABLE dbo.MetricInputStream
(
    MetricInputStreamRowId bigint IDENTITY(1,1) NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    StreamKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_MetricInputStream
        PRIMARY KEY (MetricInputStreamRowId),

    CONSTRAINT UQ_MetricInputStream_Identity
        UNIQUE (MachineId, StreamKeyBinary)
);

CREATE TABLE dbo.MetricInputFact
(
    MetricInputFactRowId bigint IDENTITY(1,1) NOT NULL,
    MetricInputStreamRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,
    FactIdBinary varbinary(512) NOT NULL,
    FactId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MetricInputKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MetricValue nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Unit nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StartsAtUtc datetimeoffset(7) NOT NULL,
    EndsAtUtc datetimeoffset(7) NOT NULL,
    CompanyId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionLineId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionContextAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    ProductionOrderId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OperationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    PartId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OperatorId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    IsPlannedProductionTime bit NULL,
    PlannedProductionScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    SourceContextualizedActivityIntervalId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    SourceEligibilityIntervalId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    SourceQuantityEvidenceId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OccurrenceSiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OccurrenceShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OccurrenceShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OccurrenceStartsAtUtc datetimeoffset(7) NOT NULL,
    OccurrenceEndsAtUtc datetimeoffset(7) NOT NULL,
    ProductionDaySiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionBusinessDate date NOT NULL,

    CONSTRAINT PK_MetricInputFact
        PRIMARY KEY (MetricInputFactRowId),

    CONSTRAINT FK_MetricInputFact_MetricInputStream
        FOREIGN KEY (MetricInputStreamRowId)
        REFERENCES dbo.MetricInputStream (MetricInputStreamRowId),

    CONSTRAINT UQ_MetricInputFact_StreamPosition
        UNIQUE (MetricInputStreamRowId, Position),

    CONSTRAINT UQ_MetricInputFact_StreamFactIdentity
        UNIQUE (MetricInputStreamRowId, FactIdBinary),

    CONSTRAINT CK_MetricInputFact_Position_UInt64
        CHECK (
            Position >= 1
            AND Position <= 18446744073709551615
        ),

    CONSTRAINT CK_MetricInputFact_Interval
        CHECK (EndsAtUtc >= StartsAtUtc),

    CONSTRAINT CK_MetricInputFact_OccurrenceInterval
        CHECK (OccurrenceEndsAtUtc > OccurrenceStartsAtUtc),

    CONSTRAINT CK_MetricInputFact_OwnershipContainment
        CHECK (
            StartsAtUtc >= OccurrenceStartsAtUtc
            AND EndsAtUtc <= OccurrenceEndsAtUtc
        )
);

CREATE INDEX IX_MetricInputFact_OrderedRead
    ON dbo.MetricInputFact (MetricInputStreamRowId, Position)
    INCLUDE (MetricInputFactRowId, FactId, MetricInputKey, MetricValue, Unit);

CREATE TABLE dbo.MetricAggregationProcessor
(
    MetricAggregationProcessorRowId bigint IDENTITY(1,1) NOT NULL,
    ProcessorKeyBinary varbinary(512) NOT NULL,
    ProcessorKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MetricInputStreamRowId bigint NOT NULL,

    CONSTRAINT PK_MetricAggregationProcessor
        PRIMARY KEY (MetricAggregationProcessorRowId),

    CONSTRAINT UQ_MetricAggregationProcessor_Identity
        UNIQUE (ProcessorKeyBinary),

    CONSTRAINT FK_MetricAggregationProcessor_MetricInputStream
        FOREIGN KEY (MetricInputStreamRowId)
        REFERENCES dbo.MetricInputStream (MetricInputStreamRowId)
);

CREATE TABLE dbo.MetricAggregationCheckpoint
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,

    CONSTRAINT PK_MetricAggregationCheckpoint
        PRIMARY KEY (MetricAggregationProcessorRowId),

    CONSTRAINT FK_MetricAggregationCheckpoint_Processor
        FOREIGN KEY (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),

    CONSTRAINT CK_MetricAggregationCheckpoint_Position_UInt64
        CHECK (
            Position >= 1
            AND Position <= 18446744073709551615
        )
);

CREATE TABLE dbo.MetricAggregationContribution
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    MetricInputFactRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,

    CONSTRAINT PK_MetricAggregationContribution
        PRIMARY KEY (
            MetricAggregationProcessorRowId,
            MetricInputFactRowId
        ),

    CONSTRAINT UQ_MetricAggregationContribution_Position
        UNIQUE (
            MetricAggregationProcessorRowId,
            Position
        ),

    CONSTRAINT FK_MetricAggregationContribution_Processor
        FOREIGN KEY (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),

    CONSTRAINT FK_MetricAggregationContribution_Fact
        FOREIGN KEY (MetricInputFactRowId)
        REFERENCES dbo.MetricInputFact (MetricInputFactRowId),

    CONSTRAINT CK_MetricAggregationContribution_Position_UInt64
        CHECK (
            Position >= 1
            AND Position <= 18446744073709551615
        )
);

CREATE TABLE dbo.ShiftMetricAggregate
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    AggregateKeyBinary varbinary(900) NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
    ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
    MetricInputKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AggregateValue nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Unit nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    InputCount bigint NOT NULL,
    FirstInputTimestamp datetimeoffset(7) NOT NULL,
    LastInputTimestamp datetimeoffset(7) NOT NULL,

    CONSTRAINT PK_ShiftMetricAggregate
        PRIMARY KEY (
            MetricAggregationProcessorRowId,
            AggregateKeyBinary
        ),

    CONSTRAINT FK_ShiftMetricAggregate_Processor
        FOREIGN KEY (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),

    CONSTRAINT CK_ShiftMetricAggregate_ShiftInterval
        CHECK (ShiftEndsAtUtc > ShiftStartsAtUtc),

    CONSTRAINT CK_ShiftMetricAggregate_InputCount
        CHECK (InputCount > 0),

    CONSTRAINT CK_ShiftMetricAggregate_InputInterval
        CHECK (LastInputTimestamp >= FirstInputTimestamp)
);

CREATE INDEX IX_ShiftMetricAggregate_Query
    ON dbo.ShiftMetricAggregate (
        MachineId,
        ShiftStartsAtUtc,
        ShiftEndsAtUtc
    )
    INCLUDE (MetricInputKey, AggregateValue, Unit, InputCount);

CREATE TABLE dbo.ProductionDayMetricAggregate
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    AggregateKeyBinary varbinary(900) NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionBusinessDate date NOT NULL,
    MetricInputKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AggregateValue nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Unit nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    InputCount bigint NOT NULL,
    FirstInputTimestamp datetimeoffset(7) NOT NULL,
    LastInputTimestamp datetimeoffset(7) NOT NULL,

    CONSTRAINT PK_ProductionDayMetricAggregate
        PRIMARY KEY (
            MetricAggregationProcessorRowId,
            AggregateKeyBinary
        ),

    CONSTRAINT FK_ProductionDayMetricAggregate_Processor
        FOREIGN KEY (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),

    CONSTRAINT CK_ProductionDayMetricAggregate_InputCount
        CHECK (InputCount > 0),

    CONSTRAINT CK_ProductionDayMetricAggregate_InputInterval
        CHECK (LastInputTimestamp >= FirstInputTimestamp)
);

CREATE INDEX IX_ProductionDayMetricAggregate_Query
    ON dbo.ProductionDayMetricAggregate (
        MachineId,
        ProductionBusinessDate
    )
    INCLUDE (MetricInputKey, AggregateValue, Unit, InputCount);