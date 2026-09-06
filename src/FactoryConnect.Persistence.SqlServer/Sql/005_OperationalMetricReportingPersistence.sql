CREATE TABLE dbo.OperationalMetricProjectionProcessor
(
    OperationalMetricProjectionProcessorRowId bigint IDENTITY(1,1) NOT NULL,
    ProcessorKeyBinary varbinary(769) NOT NULL,
    ProcessorKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MetricAggregationProcessorRowId bigint NOT NULL,
    MetricInputStreamRowId bigint NOT NULL,
    CONSTRAINT PK_OperationalMetricProjectionProcessor PRIMARY KEY CLUSTERED
        (OperationalMetricProjectionProcessorRowId),
    CONSTRAINT UQ_OperationalMetricProjectionProcessor_ProcessorKeyBinary UNIQUE NONCLUSTERED
        (ProcessorKeyBinary),
    CONSTRAINT FK_OperationalMetricProjectionProcessor_MetricAggregationProcessor FOREIGN KEY
        (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),
    CONSTRAINT FK_OperationalMetricProjectionProcessor_MetricAggregationProcessorStream FOREIGN KEY
        (MetricAggregationProcessorRowId, MetricInputStreamRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId, MetricInputStreamRowId),
    CONSTRAINT CK_OperationalMetricProjectionProcessor_ProcessorKey CHECK
        (DATALENGTH(ProcessorKey) > 0),
    CONSTRAINT CK_OperationalMetricProjectionProcessor_ProcessorKeyBinary CHECK
        (DATALENGTH(ProcessorKeyBinary) BETWEEN 1 AND 769)
);

CREATE TABLE dbo.OperationalMetricProjectionCheckpoint
(
    OperationalMetricProjectionProcessorRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,
    CONSTRAINT PK_OperationalMetricProjectionCheckpoint PRIMARY KEY CLUSTERED
        (OperationalMetricProjectionProcessorRowId),
    CONSTRAINT FK_OperationalMetricProjectionCheckpoint_ProjectionProcessor FOREIGN KEY
        (OperationalMetricProjectionProcessorRowId)
        REFERENCES dbo.OperationalMetricProjectionProcessor (OperationalMetricProjectionProcessorRowId),
    CONSTRAINT CK_OperationalMetricProjectionCheckpoint_Position CHECK
        (Position BETWEEN 1 AND 18446744073709551615)
);

CREATE TABLE dbo.OperationalMetricProjection
(
    OperationalMetricProjectionRowId bigint IDENTITY(1,1) NOT NULL,
    OperationalMetricProjectionProcessorRowId bigint NOT NULL,
    EvaluationKeyCodecVersion smallint NOT NULL,
    EvaluationKeyHash binary(32) NOT NULL,
    EvaluationKeyBinary varbinary(max) NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    MachineOrderKey AS CONVERT(binary(16), REPLACE(CONVERT(char(36), MachineId), '-', ''), 2) PERSISTED NOT NULL,
    PeriodKind tinyint NOT NULL,
    PeriodSiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PeriodSiteOrderKey varbinary(769) NOT NULL,
    ShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    ShiftScheduleAssignmentOrderKey varbinary(769) NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    ShiftOrderKey varbinary(769) NULL,
    ShiftStartsAtUtc datetimeoffset(7) NULL,
    ShiftEndsAtUtc datetimeoffset(7) NULL,
    ProductionBusinessDate date NULL,
    ProductionOrderPresent bit NOT NULL,
    ProductionOrderId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    ProductionOrderOrderKey varbinary(769) NULL,
    OperationPresent bit NOT NULL,
    OperationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OperationOrderKey varbinary(769) NULL,
    PartPresent bit NOT NULL,
    PartId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    PartOrderKey varbinary(769) NULL,
    OperatorPresent bit NOT NULL,
    OperatorId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OperatorOrderKey varbinary(769) NULL,
    MetricKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MetricKeyOrderKey varbinary(769) NOT NULL,
    DefinitionVersion nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DefinitionVersionOrderKey varbinary(769) NOT NULL,
    Status tinyint NOT NULL,
    MetricValue nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    Unit nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReasonCode tinyint NULL,
    ReasonOperandName nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    SourceRevisionPosition decimal(20,0) NOT NULL,
    CONSTRAINT PK_OperationalMetricProjection PRIMARY KEY CLUSTERED
        (OperationalMetricProjectionRowId),
    CONSTRAINT UQ_OperationalMetricProjection_LogicalHash UNIQUE NONCLUSTERED
        (OperationalMetricProjectionProcessorRowId, EvaluationKeyHash),
    CONSTRAINT UQ_OperationalMetricProjection_ProcessorRow UNIQUE NONCLUSTERED
        (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId),
    CONSTRAINT FK_OperationalMetricProjection_ProjectionProcessor FOREIGN KEY
        (OperationalMetricProjectionProcessorRowId)
        REFERENCES dbo.OperationalMetricProjectionProcessor (OperationalMetricProjectionProcessorRowId),
    CONSTRAINT CK_OperationalMetricProjection_EvaluationKeyCodecVersion CHECK
        (EvaluationKeyCodecVersion = 1),
    CONSTRAINT CK_OperationalMetricProjection_EvaluationKeyBinaryLength CHECK
        (DATALENGTH(EvaluationKeyBinary) BETWEEN 1 AND 6992),
    CONSTRAINT CK_OperationalMetricProjection_PeriodShape CHECK
    (
        (
            PeriodKind = 1
            AND PeriodSiteId IS NOT NULL
            AND DATALENGTH(PeriodSiteId) > 0
            AND PeriodSiteOrderKey IS NOT NULL
            AND DATALENGTH(PeriodSiteOrderKey) BETWEEN 1 AND 769
            AND ShiftScheduleAssignmentId IS NOT NULL
            AND DATALENGTH(ShiftScheduleAssignmentId) > 0
            AND ShiftScheduleAssignmentOrderKey IS NOT NULL
            AND DATALENGTH(ShiftScheduleAssignmentOrderKey) BETWEEN 1 AND 769
            AND ShiftId IS NOT NULL
            AND DATALENGTH(ShiftId) > 0
            AND ShiftOrderKey IS NOT NULL
            AND DATALENGTH(ShiftOrderKey) BETWEEN 1 AND 769
            AND ShiftStartsAtUtc IS NOT NULL
            AND ShiftEndsAtUtc IS NOT NULL
            AND DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0
            AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
            AND ShiftEndsAtUtc > ShiftStartsAtUtc
            AND ProductionBusinessDate IS NULL
        )
        OR
        (
            PeriodKind = 2
            AND PeriodSiteId IS NOT NULL
            AND DATALENGTH(PeriodSiteId) > 0
            AND PeriodSiteOrderKey IS NOT NULL
            AND DATALENGTH(PeriodSiteOrderKey) BETWEEN 1 AND 769
            AND ProductionBusinessDate IS NOT NULL
            AND ShiftScheduleAssignmentId IS NULL
            AND ShiftScheduleAssignmentOrderKey IS NULL
            AND ShiftId IS NULL
            AND ShiftOrderKey IS NULL
            AND ShiftStartsAtUtc IS NULL
            AND ShiftEndsAtUtc IS NULL
        )
    ),
    CONSTRAINT CK_OperationalMetricProjection_ProductionOrderContext CHECK
    (
        (ProductionOrderPresent = 0 AND ProductionOrderId IS NULL AND ProductionOrderOrderKey IS NULL)
        OR
        (ProductionOrderPresent = 1 AND ProductionOrderId IS NOT NULL AND DATALENGTH(ProductionOrderId) > 0
            AND ProductionOrderOrderKey IS NOT NULL AND DATALENGTH(ProductionOrderOrderKey) BETWEEN 1 AND 769)
    ),
    CONSTRAINT CK_OperationalMetricProjection_OperationContext CHECK
    (
        (OperationPresent = 0 AND OperationId IS NULL AND OperationOrderKey IS NULL)
        OR
        (OperationPresent = 1 AND OperationId IS NOT NULL AND DATALENGTH(OperationId) > 0
            AND OperationOrderKey IS NOT NULL AND DATALENGTH(OperationOrderKey) BETWEEN 1 AND 769)
    ),
    CONSTRAINT CK_OperationalMetricProjection_PartContext CHECK
    (
        (PartPresent = 0 AND PartId IS NULL AND PartOrderKey IS NULL)
        OR
        (PartPresent = 1 AND PartId IS NOT NULL AND DATALENGTH(PartId) > 0
            AND PartOrderKey IS NOT NULL AND DATALENGTH(PartOrderKey) BETWEEN 1 AND 769)
    ),
    CONSTRAINT CK_OperationalMetricProjection_OperatorContext CHECK
    (
        (OperatorPresent = 0 AND OperatorId IS NULL AND OperatorOrderKey IS NULL)
        OR
        (OperatorPresent = 1 AND OperatorId IS NOT NULL AND DATALENGTH(OperatorId) > 0
            AND OperatorOrderKey IS NOT NULL AND DATALENGTH(OperatorOrderKey) BETWEEN 1 AND 769)
    ),
    CONSTRAINT CK_OperationalMetricProjection_MetricIdentity CHECK
    (
        DATALENGTH(MetricKey) > 0
        AND DATALENGTH(MetricKeyOrderKey) BETWEEN 1 AND 769
        AND DATALENGTH(DefinitionVersion) > 0
        AND DATALENGTH(DefinitionVersionOrderKey) BETWEEN 1 AND 769
        AND DATALENGTH(Unit) > 0
    ),
    CONSTRAINT CK_OperationalMetricProjection_SourceRevisionPosition CHECK
        (SourceRevisionPosition BETWEEN 1 AND 18446744073709551615),
    CONSTRAINT CK_OperationalMetricProjection_Status CHECK
        (Status IN (0,1,2)),
    CONSTRAINT CK_OperationalMetricProjection_ReasonCode CHECK
        (ReasonCode IS NULL OR ReasonCode IN (0,1,2,3,4,5)),
    CONSTRAINT CK_OperationalMetricProjection_StatusShape CHECK
    (
        (
            Status = 0
            AND MetricValue IS NOT NULL
            AND DATALENGTH(MetricValue) > 0
            AND ReasonCode IS NULL
            AND ReasonOperandName IS NULL
        )
        OR
        (
            Status IN (1,2)
            AND MetricValue IS NULL
            AND ReasonCode IS NOT NULL
            AND ReasonCode IN (0,1,2,3,4,5)
            AND (ReasonOperandName IS NULL OR DATALENGTH(ReasonOperandName) > 0)
        )
    )
);

CREATE NONCLUSTERED INDEX IX_OperationalMetricProjection_ShiftWindow
    ON dbo.OperationalMetricProjection
    (OperationalMetricProjectionProcessorRowId, ShiftStartsAtUtc, MachineOrderKey, OperationalMetricProjectionRowId)
    WHERE PeriodKind = 1;

CREATE NONCLUSTERED INDEX IX_OperationalMetricProjection_ProductionDayWindow
    ON dbo.OperationalMetricProjection
    (OperationalMetricProjectionProcessorRowId, ProductionBusinessDate, MachineOrderKey, OperationalMetricProjectionRowId)
    WHERE PeriodKind = 2;

CREATE TABLE dbo.OperationalMetricProjectionManifest
(
    OperationalMetricProjectionProcessorRowId bigint NOT NULL,
    OperationalMetricProjectionRowId bigint NOT NULL,
    CONSTRAINT PK_OperationalMetricProjectionManifest PRIMARY KEY CLUSTERED
        (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId),
    CONSTRAINT FK_OperationalMetricProjectionManifest_Checkpoint FOREIGN KEY
        (OperationalMetricProjectionProcessorRowId)
        REFERENCES dbo.OperationalMetricProjectionCheckpoint (OperationalMetricProjectionProcessorRowId),
    CONSTRAINT FK_OperationalMetricProjectionManifest_Projection FOREIGN KEY
        (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId)
        REFERENCES dbo.OperationalMetricProjection
            (OperationalMetricProjectionProcessorRowId, OperationalMetricProjectionRowId)
);

CREATE TABLE dbo.OperationalMetricProjectionEvidence
(
    OperationalMetricProjectionEvidenceRowId bigint IDENTITY(1,1) NOT NULL,
    OperationalMetricProjectionRowId bigint NOT NULL,
    EvidenceKind tinyint NOT NULL,
    EvidenceOrdinal int NOT NULL,
    OperandName nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OperandNameOrderKey varbinary(769) NOT NULL,
    ComponentKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    MetricDimension tinyint NULL,
    ComponentValue nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    ComponentUnit nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
    InputCount decimal(20,0) NULL,
    FirstInputTimestamp datetimeoffset(7) NULL,
    LastInputTimestamp datetimeoffset(7) NULL,
    DependencyMetricKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    DependencyDefinitionVersion nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    DependencySnapshotCodecVersion smallint NULL,
    DependencySnapshotHash binary(32) NULL,
    DependencySnapshotBinary varbinary(max) NULL,
    CONSTRAINT PK_OperationalMetricProjectionEvidence PRIMARY KEY CLUSTERED
        (OperationalMetricProjectionEvidenceRowId),
    CONSTRAINT FK_OperationalMetricProjectionEvidence_Projection FOREIGN KEY
        (OperationalMetricProjectionRowId)
        REFERENCES dbo.OperationalMetricProjection (OperationalMetricProjectionRowId),
    CONSTRAINT UQ_OperationalMetricProjectionEvidence_Operand UNIQUE NONCLUSTERED
        (OperationalMetricProjectionRowId, OperandNameOrderKey),
    CONSTRAINT UQ_OperationalMetricProjectionEvidence_Ordinal UNIQUE NONCLUSTERED
        (OperationalMetricProjectionRowId, EvidenceKind, EvidenceOrdinal),
    CONSTRAINT CK_OperationalMetricProjectionEvidence_Operand CHECK
        (DATALENGTH(OperandName) > 0 AND DATALENGTH(OperandNameOrderKey) BETWEEN 1 AND 769),
    CONSTRAINT CK_OperationalMetricProjectionEvidence_Ordinal CHECK
        (EvidenceOrdinal >= 0),
    CONSTRAINT CK_OperationalMetricProjectionEvidence_Kind CHECK
        (EvidenceKind IN (1,2)),
    CONSTRAINT CK_OperationalMetricProjectionEvidence_SubtypeShape CHECK
    (
        (
            EvidenceKind = 1
            AND ComponentKey IS NOT NULL
            AND DATALENGTH(ComponentKey) > 0
            AND MetricDimension IS NOT NULL
            AND MetricDimension IN (0,1,2)
            AND ComponentValue IS NOT NULL
            AND DATALENGTH(ComponentValue) > 0
            AND ComponentUnit IS NOT NULL
            AND DATALENGTH(ComponentUnit) > 0
            AND InputCount IS NOT NULL
            AND InputCount BETWEEN 1 AND 9223372036854775807
            AND FirstInputTimestamp IS NOT NULL
            AND LastInputTimestamp IS NOT NULL
            AND DATEPART(TZOFFSET, FirstInputTimestamp) = 0
            AND DATEPART(TZOFFSET, LastInputTimestamp) = 0
            AND LastInputTimestamp >= FirstInputTimestamp
            AND DependencyMetricKey IS NULL
            AND DependencyDefinitionVersion IS NULL
            AND DependencySnapshotCodecVersion IS NULL
            AND DependencySnapshotHash IS NULL
            AND DependencySnapshotBinary IS NULL
        )
        OR
        (
            EvidenceKind = 2
            AND ComponentKey IS NULL
            AND MetricDimension IS NULL
            AND ComponentValue IS NULL
            AND ComponentUnit IS NULL
            AND InputCount IS NULL
            AND FirstInputTimestamp IS NULL
            AND LastInputTimestamp IS NULL
            AND DependencyMetricKey IS NOT NULL
            AND DATALENGTH(DependencyMetricKey) > 0
            AND DependencyDefinitionVersion IS NOT NULL
            AND DATALENGTH(DependencyDefinitionVersion) > 0
            AND DependencySnapshotCodecVersion IS NOT NULL
            AND DependencySnapshotCodecVersion = 1
            AND DependencySnapshotHash IS NOT NULL
            AND DependencySnapshotBinary IS NOT NULL
            AND DATALENGTH(DependencySnapshotBinary) BETWEEN 1 AND 16777216
        )
    )
);

CREATE TABLE dbo.MachineShiftOccurrenceRoster
(
    MachineShiftOccurrenceRosterRowId bigint IDENTITY(1,1) NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ProductionDaySiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionDaySiteOrderKey varbinary(769) NOT NULL,
    ProductionBusinessDate date NOT NULL,
    ProductionLineId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Revision decimal(20,0) NOT NULL,
    CONSTRAINT PK_MachineShiftOccurrenceRoster PRIMARY KEY CLUSTERED
        (MachineShiftOccurrenceRosterRowId),
    CONSTRAINT UQ_MachineShiftOccurrenceRoster_Identity UNIQUE NONCLUSTERED
        (MachineId, ProductionDaySiteOrderKey, ProductionBusinessDate),
    CONSTRAINT CK_MachineShiftOccurrenceRoster_Site CHECK
        (DATALENGTH(ProductionDaySiteId) > 0 AND DATALENGTH(ProductionDaySiteOrderKey) BETWEEN 1 AND 769),
    CONSTRAINT CK_MachineShiftOccurrenceRoster_ProductionLine CHECK
        (DATALENGTH(ProductionLineId) > 0),
    CONSTRAINT CK_MachineShiftOccurrenceRoster_Revision CHECK
        (Revision BETWEEN 1 AND 18446744073709551615)
);

CREATE TABLE dbo.MachineShiftOccurrenceRosterOccurrence
(
    MachineShiftOccurrenceRosterRowId bigint NOT NULL,
    ShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftScheduleAssignmentOrderKey varbinary(769) NOT NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftOrderKey varbinary(769) NOT NULL,
    ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
    ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_MachineShiftOccurrenceRosterOccurrence PRIMARY KEY NONCLUSTERED
    (
        MachineShiftOccurrenceRosterRowId,
        ShiftScheduleAssignmentOrderKey,
        ShiftOrderKey,
        ShiftStartsAtUtc,
        ShiftEndsAtUtc
    ),
    CONSTRAINT FK_MachineShiftOccurrenceRosterOccurrence_Roster FOREIGN KEY
        (MachineShiftOccurrenceRosterRowId)
        REFERENCES dbo.MachineShiftOccurrenceRoster (MachineShiftOccurrenceRosterRowId),
    CONSTRAINT CK_MachineShiftOccurrenceRosterOccurrence_ShiftIdentity CHECK
    (
        DATALENGTH(ShiftScheduleAssignmentId) > 0
        AND DATALENGTH(ShiftScheduleAssignmentOrderKey) BETWEEN 1 AND 769
        AND DATALENGTH(ShiftId) > 0
        AND DATALENGTH(ShiftOrderKey) BETWEEN 1 AND 769
    ),
    CONSTRAINT CK_MachineShiftOccurrenceRosterOccurrence_Time CHECK
    (
        DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0
        AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
        AND ShiftEndsAtUtc > ShiftStartsAtUtc
    )
);
