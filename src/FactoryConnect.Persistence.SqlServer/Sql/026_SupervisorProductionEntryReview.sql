-- Local commissioning review decisions only. Not authenticated factory approval, CNC evidence, or OEE publication.
CREATE TABLE dbo.SupervisorProductionEntryReview
(
    EntryId uniqueidentifier NOT NULL,
    DecisionId uniqueidentifier NOT NULL,
    Decision nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Reason nvarchar(2000) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorProductionEntryReview PRIMARY KEY (EntryId),
    CONSTRAINT UQ_SupervisorProductionEntryReview_DecisionId UNIQUE (DecisionId),
    CONSTRAINT FK_SupervisorProductionEntryReview_Entry FOREIGN KEY (EntryId)
        REFERENCES dbo.SupervisorProductionEntry (EntryId),
    CONSTRAINT CK_SupervisorProductionEntryReview_Decision CHECK (Decision IN (N'Approved', N'Rejected')),
    CONSTRAINT CK_SupervisorProductionEntryReview_Reason CHECK (LEN(TRIM(Reason)) > 0),
    CONSTRAINT CK_SupervisorProductionEntryReview_Reviewer CHECK (LEN(TRIM(ReviewedBy)) > 0),
    CONSTRAINT CK_SupervisorProductionEntryReview_Utc CHECK (DATEPART(TZOFFSET, ReviewedAtUtc) = 0)
);
