CREATE TABLE dbo.ProductionAdmissionRecoveryAudit
(
    OperationId uniqueidentifier NOT NULL,
    ProductionContextProcessorRowId bigint NOT NULL,
    SchemaVersion int NOT NULL,
    BeforeBoundary datetimeoffset(7) NULL,
    AfterBoundary datetimeoffset(7) NOT NULL,
    RecordedAtUtc datetimeoffset(7) NOT NULL,
    Evidence nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_ProductionAdmissionRecoveryAudit PRIMARY KEY (OperationId),
    CONSTRAINT UQ_ProductionAdmissionRecoveryAudit_Processor UNIQUE (ProductionContextProcessorRowId)
);
