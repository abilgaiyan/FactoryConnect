CREATE TABLE dbo.OperationalMetricCoverageSubject
(
    SubjectId bigint IDENTITY(1,1) NOT NULL,
    SubjectHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    SubjectBinary varbinary(max) NOT NULL,
    MetricAggregationProcessorRowId bigint NOT NULL,
    MetricInputStreamRowId bigint NOT NULL,
    SourcePosition decimal(20,0) NOT NULL,
    CONSTRAINT PK_OperationalMetricCoverageSubject PRIMARY KEY (SubjectId),
    CONSTRAINT UQ_OperationalMetricCoverageSubject_Hash UNIQUE (SubjectHash),
    CONSTRAINT CK_OperationalMetricCoverageSubject_Codec CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_OperationalMetricCoverageSubject_Binary CHECK (DATALENGTH(SubjectBinary) > 0),
    CONSTRAINT CK_OperationalMetricCoverageSubject_Position CHECK (SourcePosition BETWEEN 1 AND 18446744073709551615),
    CONSTRAINT FK_OperationalMetricCoverageSubject_ProcessorStream
        FOREIGN KEY (MetricAggregationProcessorRowId, MetricInputStreamRowId)
        REFERENCES dbo.MetricAggregationProcessor (MetricAggregationProcessorRowId, MetricInputStreamRowId),
    CONSTRAINT FK_OperationalMetricCoverageSubject_SourceRevision
        FOREIGN KEY (MetricAggregationProcessorRowId, SourcePosition)
        REFERENCES dbo.MetricAggregationRevision (MetricAggregationProcessorRowId, Position)
);

CREATE TABLE dbo.OperationalMetricCoverageVersion
(
    SubjectId bigint NOT NULL,
    AssessmentRevision bigint NOT NULL,
    ContentCodecVersion int NOT NULL,
    ContentBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_OperationalMetricCoverageVersion PRIMARY KEY (SubjectId, AssessmentRevision),
    CONSTRAINT CK_OperationalMetricCoverageVersion_Revision CHECK (AssessmentRevision > 0),
    CONSTRAINT CK_OperationalMetricCoverageVersion_Codec CHECK (ContentCodecVersion > 0),
    CONSTRAINT CK_OperationalMetricCoverageVersion_Binary CHECK (DATALENGTH(ContentBinary) > 0),
    CONSTRAINT FK_OperationalMetricCoverageVersion_Subject FOREIGN KEY (SubjectId)
        REFERENCES dbo.OperationalMetricCoverageSubject (SubjectId)
);

CREATE TABLE dbo.OperationalMetricCoverageHead
(
    SubjectId bigint NOT NULL,
    HeadRevision bigint NOT NULL,
    CONSTRAINT PK_OperationalMetricCoverageHead PRIMARY KEY (SubjectId),
    CONSTRAINT CK_OperationalMetricCoverageHead_Revision CHECK (HeadRevision > 0),
    CONSTRAINT FK_OperationalMetricCoverageHead_Version FOREIGN KEY (SubjectId, HeadRevision)
        REFERENCES dbo.OperationalMetricCoverageVersion (SubjectId, AssessmentRevision)
);
