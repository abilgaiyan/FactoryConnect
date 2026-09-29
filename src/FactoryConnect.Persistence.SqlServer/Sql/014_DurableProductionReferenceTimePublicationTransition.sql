CREATE TABLE dbo.ProductionReferenceTimePublicationTransition
(
    MetricAggregationProcessorRowId bigint NOT NULL,
    TargetMetricAggregationPosition decimal(20,0) NOT NULL,
    ExpectedPreviousMetricAggregationPosition decimal(20,0) NULL,
    ProductionStandardAuthorityRevision decimal(20,0) NULL,
    CompletedProductionReferenceTimeRevision decimal(20,0) NULL,
    IsCompleted bit NOT NULL,

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

    CONSTRAINT FK_ProductionReferenceTimePublicationTransition_CompletedReferenceRevision
        FOREIGN KEY
        (
            MetricAggregationProcessorRowId,
            CompletedProductionReferenceTimeRevision
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

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_CompletedReferenceRevision
        CHECK
        (
            CompletedProductionReferenceTimeRevision IS NULL
            OR
            (
                CompletedProductionReferenceTimeRevision >= 0
                AND CompletedProductionReferenceTimeRevision <= 18446744073709551615
            )
        ),

    CONSTRAINT CK_ProductionReferenceTimePublicationTransition_State
        CHECK
        (
            (IsCompleted = 0 AND CompletedProductionReferenceTimeRevision IS NULL)
            OR
            (IsCompleted = 1 AND CompletedProductionReferenceTimeRevision IS NOT NULL)
        )
);
