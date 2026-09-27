CREATE TABLE dbo.ProductionReferenceTimeRevision
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_ProductionReferenceTimeRevision
        PRIMARY KEY (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision),

    CONSTRAINT FK_ProductionReferenceTimeRevision_AggregationAuthority
        FOREIGN KEY (MetricAggregationProcessorRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId),

    CONSTRAINT CK_ProductionReferenceTimeRevision_UInt64
        CHECK (
            ProductionReferenceTimeRevision >= 0
            AND ProductionReferenceTimeRevision <= 18446744073709551615
        )
);

CREATE TABLE dbo.ProductionReferenceTimeOutcome
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,
    SourceQuantityEvidenceId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CompanyId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftOccurrenceSiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftScheduleAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
    ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
    ProductionDaySiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionBusinessDate date NOT NULL,
    OperationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    PartId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OccurredAtUtc datetimeoffset(7) NOT NULL,
    ProducedQuantity int NOT NULL,
    ProductionStandardAuthorityRevision decimal(20,0) NOT NULL,
    ResolutionStatus tinyint NOT NULL,
    SelectedStandardVersionId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    SelectedStandardSourceReference nvarchar(1024) COLLATE Latin1_General_100_BIN2 NULL,
    IdealProductionDurationSeconds decimal(20,6) NULL,

    CONSTRAINT PK_ProductionReferenceTimeOutcome
        PRIMARY KEY (
            MetricAggregationProcessorRowId,
            ProductionReferenceTimeRevision,
            SourceQuantityEvidenceId
        ),

    CONSTRAINT FK_ProductionReferenceTimeOutcome_Revision
        FOREIGN KEY (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
        REFERENCES dbo.ProductionReferenceTimeRevision
            (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision),

    CONSTRAINT UQ_ProductionReferenceTimeOutcome_SourceReplay
        UNIQUE (MetricAggregationProcessorRowId, SourceQuantityEvidenceId),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_Revision_UInt64
        CHECK (
            ProductionReferenceTimeRevision >= 0
            AND ProductionReferenceTimeRevision <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_StandardRevision_UInt64
        CHECK (
            ProductionStandardAuthorityRevision >= 0
            AND ProductionStandardAuthorityRevision <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_ProducedQuantity
        CHECK (ProducedQuantity >= 0),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_Status
        CHECK (ResolutionStatus >= 0 AND ResolutionStatus <= 3),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_PeriodOwnership
        CHECK (
            SiteId = ShiftOccurrenceSiteId
            AND SiteId = ProductionDaySiteId
            AND ShiftEndsAtUtc > ShiftStartsAtUtc
            AND OccurredAtUtc >= ShiftStartsAtUtc
            AND OccurredAtUtc < ShiftEndsAtUtc
        ),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_Utc
        CHECK (
            datepart(tzoffset, OccurredAtUtc) = 0
            AND datepart(tzoffset, ShiftStartsAtUtc) = 0
            AND datepart(tzoffset, ShiftEndsAtUtc) = 0
        ),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_IdealDuration
        CHECK (
            IdealProductionDurationSeconds IS NULL
            OR IdealProductionDurationSeconds >= 0
        )
);

CREATE TABLE dbo.ProductionReferenceTimeOutcomeConflict
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,
    SourceQuantityEvidenceId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ConflictingStandardVersionId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_ProductionReferenceTimeOutcomeConflict
        PRIMARY KEY (
            MetricAggregationProcessorRowId,
            ProductionReferenceTimeRevision,
            SourceQuantityEvidenceId,
            ConflictingStandardVersionId
        ),

    CONSTRAINT FK_ProductionReferenceTimeOutcomeConflict_Outcome
        FOREIGN KEY (
            MetricAggregationProcessorRowId,
            ProductionReferenceTimeRevision,
            SourceQuantityEvidenceId
        )
        REFERENCES dbo.ProductionReferenceTimeOutcome
            (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision, SourceQuantityEvidenceId)
);

CREATE TABLE dbo.ProductionReferenceTimePublicationCut
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    MetricAggregationPosition decimal(20,0) NOT NULL,
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_ProductionReferenceTimePublicationCut
        PRIMARY KEY (
            MetricAggregationProcessorRowId,
            MetricAggregationPosition,
            ProductionReferenceTimeRevision
        ),

    CONSTRAINT FK_ProductionReferenceTimePublicationCut_MetricAggregationRevision
        FOREIGN KEY (MetricAggregationProcessorRowId, MetricAggregationPosition)
        REFERENCES dbo.MetricAggregationRevision
            (MetricAggregationProcessorRowId, Position),

    CONSTRAINT FK_ProductionReferenceTimePublicationCut_ReferenceTimeRevision
        FOREIGN KEY (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
        REFERENCES dbo.ProductionReferenceTimeRevision
            (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision),

    CONSTRAINT CK_ProductionReferenceTimePublicationCut_MetricPosition_UInt64Positive
        CHECK (
            MetricAggregationPosition >= 1
            AND MetricAggregationPosition <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationCut_ReferenceRevision_UInt64
        CHECK (
            ProductionReferenceTimeRevision >= 0
            AND ProductionReferenceTimeRevision <= 18446744073709551615
        )
);

-- Every existing aggregation authority receives a durable empty reference-time
-- revision. This makes (A, R0) a real ledger state rather than an implicit reader
-- convention. Existing immutable aggregation revisions are paired with that empty
-- cut without inventing outcomes.
INSERT INTO dbo.ProductionReferenceTimeRevision
    (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
SELECT MetricAggregationProcessorRowId, 0
FROM dbo.MetricAggregationProcessor;

INSERT INTO dbo.ProductionReferenceTimePublicationCut
    (MetricAggregationProcessorRowId, MetricAggregationPosition, ProductionReferenceTimeRevision)
SELECT MetricAggregationProcessorRowId, Position, 0
FROM dbo.MetricAggregationRevision;
