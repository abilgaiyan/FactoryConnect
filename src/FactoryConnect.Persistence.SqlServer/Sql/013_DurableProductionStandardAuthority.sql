CREATE TABLE dbo.ProductionStandardAuthorityRevision
(
    ProductionStandardAuthorityRevision decimal(20,0) NOT NULL,

    CONSTRAINT PK_ProductionStandardAuthorityRevision
        PRIMARY KEY (ProductionStandardAuthorityRevision),

    CONSTRAINT CK_ProductionStandardAuthorityRevision_UInt64
        CHECK (
            ProductionStandardAuthorityRevision >= 0
            AND ProductionStandardAuthorityRevision <= 18446744073709551615
        )
);

CREATE TABLE dbo.ProductionStandardVersion
(
    VersionId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductionStandardAuthorityRevision decimal(20,0) NOT NULL,
    CompanyId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SiteId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PartId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OperationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    MachineId uniqueidentifier NULL,
    SecondsPerUnit decimal(20,6) NOT NULL,
    EffectiveFromUtc datetimeoffset(7) NOT NULL,
    EffectiveToUtc datetimeoffset(7) NULL,
    SourceReference nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,

    CONSTRAINT PK_ProductionStandardVersion
        PRIMARY KEY (VersionId),

    CONSTRAINT UQ_ProductionStandardVersion_Revision
        UNIQUE (ProductionStandardAuthorityRevision),

    CONSTRAINT FK_ProductionStandardVersion_AuthorityRevision
        FOREIGN KEY (ProductionStandardAuthorityRevision)
        REFERENCES dbo.ProductionStandardAuthorityRevision (ProductionStandardAuthorityRevision),

    CONSTRAINT CK_ProductionStandardVersion_RevisionPositive
        CHECK (
            ProductionStandardAuthorityRevision >= 1
            AND ProductionStandardAuthorityRevision <= 18446744073709551615
        ),

    CONSTRAINT CK_ProductionStandardVersion_SecondsPerUnit
        CHECK (SecondsPerUnit >= 0),

    CONSTRAINT CK_ProductionStandardVersion_EffectiveInterval
        CHECK (EffectiveToUtc IS NULL OR EffectiveToUtc > EffectiveFromUtc),

    CONSTRAINT CK_ProductionStandardVersion_Utc
        CHECK (
            datepart(tzoffset, EffectiveFromUtc) = 0
            AND (EffectiveToUtc IS NULL OR datepart(tzoffset, EffectiveToUtc) = 0)
        )
);

-- S0 is an explicit, durable empty cut. Migration deliberately does not infer
-- historical approved standards from Migration-012 outcome lineage.
INSERT INTO dbo.ProductionStandardAuthorityRevision
    (ProductionStandardAuthorityRevision)
VALUES (0);
