-- Append-only correction history for local supervisor declarations. No production quantity or OEE publication.
CREATE TABLE dbo.SupervisorProductionEntryRevision
(
    RevisionId uniqueidentifier NOT NULL,
    EntryId uniqueidentifier NOT NULL,
    PreviousRevisionId uniqueidentifier NULL,
    RevisionNumber int NOT NULL,
    ProductionOrder nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Operation nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Note nvarchar(2000) COLLATE Latin1_General_100_BIN2 NULL,
    GoodQuantity bigint NULL,
    RejectedQuantity bigint NULL,
    CorrectionReason nvarchar(2000) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RevisedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RevisedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorProductionEntryRevision PRIMARY KEY (RevisionId),
    CONSTRAINT UQ_SupervisorProductionEntryRevision_EntryNumber UNIQUE (EntryId, RevisionNumber),
    CONSTRAINT FK_SupervisorProductionEntryRevision_Entry FOREIGN KEY (EntryId)
        REFERENCES dbo.SupervisorProductionEntry (EntryId),
    CONSTRAINT CK_SupervisorProductionEntryRevision_Number CHECK (RevisionNumber > 0),
    CONSTRAINT CK_SupervisorProductionEntryRevision_Quantity CHECK
        ((GoodQuantity IS NULL AND RejectedQuantity IS NULL) OR
         (GoodQuantity IS NOT NULL AND RejectedQuantity IS NOT NULL
          AND GoodQuantity >= 0 AND RejectedQuantity >= 0)),
    CONSTRAINT CK_SupervisorProductionEntryRevision_Required CHECK
        (LEN(TRIM(ProductionOrder)) > 0 AND LEN(TRIM(PartNumber)) > 0
         AND LEN(TRIM(Operation)) > 0 AND LEN(TRIM(CorrectionReason)) > 0
         AND LEN(TRIM(RevisedBy)) > 0),
    CONSTRAINT CK_SupervisorProductionEntryRevision_Utc CHECK (DATEPART(TZOFFSET, RevisedAtUtc) = 0)
);
