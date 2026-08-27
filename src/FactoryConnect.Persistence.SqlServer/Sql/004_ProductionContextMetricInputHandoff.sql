CREATE TABLE dbo.ProductionContextProcessor
(
    ProductionContextProcessorRowId bigint IDENTITY(1,1) NOT NULL,
    ProcessorKeyHash binary(32) NOT NULL,
    ProcessorKeyBinary varbinary(512) NOT NULL,
    ProcessorKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ObservationStreamKeyHash binary(32) NOT NULL,
    ObservationStreamKeyBinary varbinary(512) NOT NULL,
    ObservationStreamKey nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_ProductionContextProcessor
        PRIMARY KEY (ProductionContextProcessorRowId),

    CONSTRAINT UQ_ProductionContextProcessor_Identity
        UNIQUE (
            ProcessorKeyHash,
            MachineId,
            ObservationStreamKeyHash
        )
);

CREATE TABLE dbo.ProductionContextCheckpoint
(
    ProductionContextProcessorRowId bigint NOT NULL,
    Position decimal(20,0) NOT NULL,

    CONSTRAINT PK_ProductionContextCheckpoint
        PRIMARY KEY (ProductionContextProcessorRowId),

    CONSTRAINT FK_ProductionContextCheckpoint_Processor
        FOREIGN KEY (ProductionContextProcessorRowId)
        REFERENCES dbo.ProductionContextProcessor (ProductionContextProcessorRowId),

    CONSTRAINT CK_ProductionContextCheckpoint_Position_UInt64
        CHECK (
            Position >= 1
            AND Position <= 18446744073709551615
        )
);

CREATE TABLE dbo.ContextualizedActivityOutput
(
    ContextualizedActivityOutputRowId bigint IDENTITY(1,1) NOT NULL,
    IdentityHash binary(32) NOT NULL,
    IdentityBinary varbinary(512) NOT NULL,
    IdentityText nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PayloadHash binary(32) NOT NULL,
    Payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_ContextualizedActivityOutput
        PRIMARY KEY (ContextualizedActivityOutputRowId),

    CONSTRAINT UQ_ContextualizedActivityOutput_IdentityHash
        UNIQUE (IdentityHash)
);

CREATE TABLE dbo.ProductionTimeEligibilityOutput
(
    ProductionTimeEligibilityOutputRowId bigint IDENTITY(1,1) NOT NULL,
    IdentityHash binary(32) NOT NULL,
    IdentityBinary varbinary(512) NOT NULL,
    IdentityText nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PayloadHash binary(32) NOT NULL,
    Payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_ProductionTimeEligibilityOutput
        PRIMARY KEY (ProductionTimeEligibilityOutputRowId),

    CONSTRAINT UQ_ProductionTimeEligibilityOutput_IdentityHash
        UNIQUE (IdentityHash)
);
