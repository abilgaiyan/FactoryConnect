CREATE TABLE dbo.MetricAggregationRevision
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,

    CONSTRAINT PK_MetricAggregationRevision
        PRIMARY KEY CLUSTERED (MetricAggregationProcessorRowId, Position),

    CONSTRAINT FK_MetricAggregationRevision_Processor
        FOREIGN KEY (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),

    CONSTRAINT CK_MetricAggregationRevision_Position_UInt64Positive
        CHECK (Position BETWEEN 1 AND 18446744073709551615)
);

CREATE TABLE dbo.MetricAggregationRevisionShiftOccurrence
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,
    ShiftOccurrenceIdentityHash binary(32) NOT NULL,
    ShiftOccurrenceIdentityBinary varbinary(max) NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SiteOrderKey varbinary(769) NOT NULL,
    ShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftScheduleAssignmentOrderKey varbinary(769) NOT NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftOrderKey varbinary(769) NOT NULL,
    ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
    ShiftEndsAtUtc datetimeoffset(7) NOT NULL,

    CONSTRAINT PK_MetricAggregationRevisionShiftOccurrence
        PRIMARY KEY CLUSTERED
        (MetricAggregationProcessorRowId, Position, ShiftOccurrenceIdentityHash),

    CONSTRAINT FK_MetricAggregationRevisionShiftOccurrence_Revision
        FOREIGN KEY (MetricAggregationProcessorRowId, Position)
        REFERENCES dbo.MetricAggregationRevision
            (MetricAggregationProcessorRowId, Position),

    CONSTRAINT CK_MetricAggregationRevisionShiftOccurrence_Identity
        CHECK
        (
            DATALENGTH(ShiftOccurrenceIdentityBinary) > 0
            AND DATALENGTH(SiteId) > 0
            AND DATALENGTH(SiteOrderKey) BETWEEN 1 AND 769
            AND DATALENGTH(ShiftScheduleAssignmentId) > 0
            AND DATALENGTH(ShiftScheduleAssignmentOrderKey) BETWEEN 1 AND 769
            AND DATALENGTH(ShiftId) > 0
            AND DATALENGTH(ShiftOrderKey) BETWEEN 1 AND 769
        ),

    CONSTRAINT CK_MetricAggregationRevisionShiftOccurrence_Time
        CHECK
        (
            DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0
            AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
            AND ShiftEndsAtUtc > ShiftStartsAtUtc
        )
);

CREATE TABLE dbo.MetricAggregationRevisionProductionDay
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SiteOrderKey varbinary(769) NOT NULL,
    ProductionBusinessDate date NOT NULL,

    CONSTRAINT PK_MetricAggregationRevisionProductionDay
        PRIMARY KEY CLUSTERED
        (MetricAggregationProcessorRowId, Position, SiteOrderKey, ProductionBusinessDate),

    CONSTRAINT FK_MetricAggregationRevisionProductionDay_Revision
        FOREIGN KEY (MetricAggregationProcessorRowId, Position)
        REFERENCES dbo.MetricAggregationRevision
            (MetricAggregationProcessorRowId, Position),

    CONSTRAINT CK_MetricAggregationRevisionProductionDay_Site
        CHECK
        (
            DATALENGTH(SiteId) > 0
            AND DATALENGTH(SiteOrderKey) BETWEEN 1 AND 769
        )
);
