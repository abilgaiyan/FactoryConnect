CREATE TABLE dbo.ProductionReferenceTimePublicationTransition
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    TargetMetricAggregationPosition decimal(20,0) NOT NULL,
    ExpectedPreviousMetricAggregationPosition decimal(20,0) NULL,
    ProductionStandardAuthorityRevision decimal(20,0) NULL,
    StartingProductionReferenceTimeRevision decimal(20,0) NOT NULL,
    State tinyint NOT NULL,
    FinalProductionReferenceTimeRevision decimal(20,0) NULL,

    CONSTRAINT PK_ProductionReferenceTimePublicationTransition
        PRIMARY KEY
        (
            MetricAggregationProcessorRowId,
            TargetMetricAggregationPosition
        ),

    CONSTRAINT FK_ProductionReferenceTimePublicationTransition_TargetAggregation
        FOREIGN KEY
        (
            MetricAggregationProcessorRowId,
            TargetMetricAggregationPosition
        )
        REFERENCES dbo.MetricAggregationRevision
        (
            MetricAggregationProcessorRowId,
            Position
        ),

    CONSTRAINT FK_ProductionReferenceTimePublicationTransition_PreviousAggregation
        FOREIGN KEY
        (
            MetricAggregationProcessorRowId,
            ExpectedPreviousMetricAggregationPosition
        )
        REFERENCES dbo.MetricAggregationRevision
        (
            MetricAggregationProcessorRowId,
            Position
        ),

    CONSTRAINT FK_ProductionReferenceTimePublicationTransition_StandardRevision
        FOREIGN KEY (ProductionStandardAuthorityRevision)
        REFERENCES dbo.ProductionStandardAuthorityRevision
            (ProductionStandardAuthorityRevision),

    CONSTRAINT FK_ProductionReferenceTimePublicationTransition_StartingReferenceRevision
        FOREIGN KEY
        (
            MetricAggregationProcessorRowId,
            StartingProductionReferenceTimeRevision
        )
        REFERENCES dbo.ProductionReferenceTimeRevision
        (
            MetricAggregationProcessorRowId,
            ProductionReferenceTimeRevision
        ),

    CONSTRAINT FK_ProductionReferenceTimePublicationTransition_FinalReferenceRevision
        FOREIGN KEY
        (
            MetricAggregationProcessorRowId,
            FinalProductionReferenceTimeRevision
        )
        REFERENCES dbo.ProductionReferenceTimeRevision
        (
            MetricAggregationProcessorRowId,
            ProductionReferenceTimeRevision
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_TargetPosition
        CHECK
        (
            TargetMetricAggregationPosition >= 1
            AND TargetMetricAggregationPosition <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_PreviousPosition
        CHECK
        (
            ExpectedPreviousMetricAggregationPosition IS NULL
            OR
            (
                ExpectedPreviousMetricAggregationPosition >= 1
                AND ExpectedPreviousMetricAggregationPosition < TargetMetricAggregationPosition
                AND ExpectedPreviousMetricAggregationPosition <= 18446744073709551615
            )
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_StandardRevision
        CHECK
        (
            ProductionStandardAuthorityRevision IS NULL
            OR
            (
                ProductionStandardAuthorityRevision >= 0
                AND ProductionStandardAuthorityRevision <= 18446744073709551615
            )
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_StartingReferenceRevision
        CHECK
        (
            StartingProductionReferenceTimeRevision >= 0
            AND StartingProductionReferenceTimeRevision <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_FinalReferenceRevision
        CHECK
        (
            FinalProductionReferenceTimeRevision IS NULL
            OR
            (
                FinalProductionReferenceTimeRevision >= 0
                AND FinalProductionReferenceTimeRevision <= 18446744073709551615
            )
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_State
        CHECK
        (
            (State = 0 AND FinalProductionReferenceTimeRevision IS NULL)
            OR
            (State = 1 AND FinalProductionReferenceTimeRevision IS NOT NULL)
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_CompletionRevision
        CHECK
        (
            State = 0
            OR
            (
                ProductionStandardAuthorityRevision IS NULL
                AND FinalProductionReferenceTimeRevision = StartingProductionReferenceTimeRevision
            )
            OR
            (
                ProductionStandardAuthorityRevision IS NOT NULL
                AND FinalProductionReferenceTimeRevision > StartingProductionReferenceTimeRevision
            )
        )
);

CREATE UNIQUE INDEX UX_ProductionReferenceTimePublicationTransition_Pending
    ON dbo.ProductionReferenceTimePublicationTransition
        (MetricAggregationProcessorRowId)
    WHERE State = 0;

CREATE UNIQUE INDEX UX_ProductionReferenceTimePublicationTransition_Successor
    ON dbo.ProductionReferenceTimePublicationTransition
        (MetricAggregationProcessorRowId, ExpectedPreviousMetricAggregationPosition)
    WHERE ExpectedPreviousMetricAggregationPosition IS NOT NULL;

CREATE UNIQUE INDEX UX_ProductionReferenceTimePublicationTransition_BootstrapSuccessor
    ON dbo.ProductionReferenceTimePublicationTransition
        (MetricAggregationProcessorRowId)
    WHERE ExpectedPreviousMetricAggregationPosition IS NULL;
