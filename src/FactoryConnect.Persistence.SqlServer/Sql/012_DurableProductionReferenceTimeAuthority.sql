CREATE TABLE dbo.ProductionReferenceTimeRevision
(
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_ProductionReferenceTimeRevision
        PRIMARY KEY (ProductionReferenceTimeRevision),

    CONSTRAINT CK_ProductionReferenceTimeRevision_UInt64Positive
        CHECK (
            ProductionReferenceTimeRevision >= 1
            AND ProductionReferenceTimeRevision <= 18446744073709551615
        )
);

CREATE TABLE dbo.ProductionReferenceTimeOutcome
(
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,
    SourceQuantityEvidenceId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CompanyId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionContextAssignmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    ProductionOrderId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OperationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    PartId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OperatorId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    OccurredAtUtc datetimeoffset(7) NOT NULL,
    ProducedQuantity int NOT NULL,
    ProductionStandardAuthorityRevision decimal(20,0) NOT NULL,
    ResolutionStatus tinyint NOT NULL,
    SelectedStandardVersionId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
    SelectedStandardEffectiveFromUtc datetimeoffset(7) NULL,
    IdealProductionDurationSeconds decimal(20,6) NULL,

    CONSTRAINT PK_ProductionReferenceTimeOutcome
        PRIMARY KEY (ProductionReferenceTimeRevision, SourceQuantityEvidenceId),

    CONSTRAINT FK_ProductionReferenceTimeOutcome_Revision
        FOREIGN KEY (ProductionReferenceTimeRevision)
        REFERENCES dbo.ProductionReferenceTimeRevision (ProductionReferenceTimeRevision),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_Revision_UInt64Positive
        CHECK (
            ProductionReferenceTimeRevision >= 1
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

    CONSTRAINT CK_ProductionReferenceTimeOutcome_Utc
        CHECK (
            datepart(tzoffset, OccurredAtUtc) = 0
            AND (
                SelectedStandardEffectiveFromUtc IS NULL
                OR datepart(tzoffset, SelectedStandardEffectiveFromUtc) = 0
            )
        ),

    CONSTRAINT CK_ProductionReferenceTimeOutcome_IdealDuration
        CHECK (
            IdealProductionDurationSeconds IS NULL
            OR IdealProductionDurationSeconds >= 0
        )
);

CREATE TABLE dbo.ProductionReferenceTimeOutcomeConflict
(
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,
    SourceQuantityEvidenceId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ConflictingStandardVersionId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_ProductionReferenceTimeOutcomeConflict
        PRIMARY KEY (
            ProductionReferenceTimeRevision,
            SourceQuantityEvidenceId,
            ConflictingStandardVersionId
        ),

    CONSTRAINT FK_ProductionReferenceTimeOutcomeConflict_Outcome
        FOREIGN KEY (ProductionReferenceTimeRevision, SourceQuantityEvidenceId)
        REFERENCES dbo.ProductionReferenceTimeOutcome
            (ProductionReferenceTimeRevision, SourceQuantityEvidenceId)
);

CREATE TABLE dbo.ProductionReferenceTimePublicationCut
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    MetricAggregationPosition decimal(20,0) NOT NULL,
    ProductionReferenceTimeRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_ProductionReferenceTimePublicationCut
        PRIMARY KEY (MetricAggregationProcessorRowId, MetricAggregationPosition),

    CONSTRAINT FK_ProductionReferenceTimePublicationCut_MetricAggregationRevision
        FOREIGN KEY (MetricAggregationProcessorRowId, MetricAggregationPosition)
        REFERENCES dbo.MetricAggregationRevision
            (MetricAggregationProcessorRowId, Position),

    CONSTRAINT FK_ProductionReferenceTimePublicationCut_ReferenceTimeRevision
        FOREIGN KEY (ProductionReferenceTimeRevision)
        REFERENCES dbo.ProductionReferenceTimeRevision (ProductionReferenceTimeRevision),

    CONSTRAINT CK_ProductionReferenceTimePublicationCut_MetricPosition_UInt64Positive
        CHECK (
            MetricAggregationPosition >= 1
            AND MetricAggregationPosition <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationCut_ReferenceRevision_UInt64Positive
        CHECK (
            ProductionReferenceTimeRevision >= 1
            AND ProductionReferenceTimeRevision <= 18446744073709551615
        )
);
