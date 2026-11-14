-- Independent immutable local review of exact correction revisions.
CREATE TABLE dbo.SupervisorProductionEntryRevisionReview
(
    RevisionId uniqueidentifier NOT NULL,
    DecisionId uniqueidentifier NOT NULL,
    Decision nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Reason nvarchar(2000) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorProductionEntryRevisionReview PRIMARY KEY (RevisionId),
    CONSTRAINT UQ_SupervisorProductionEntryRevisionReview_DecisionId UNIQUE (DecisionId),
    CONSTRAINT FK_SupervisorProductionEntryRevisionReview_Revision FOREIGN KEY (RevisionId)
        REFERENCES dbo.SupervisorProductionEntryRevision (RevisionId),
    CONSTRAINT CK_SupervisorProductionEntryRevisionReview_Decision CHECK (Decision IN (N'Approved', N'Rejected')),
    CONSTRAINT CK_SupervisorProductionEntryRevisionReview_Reason CHECK (LEN(TRIM(Reason)) > 0),
    CONSTRAINT CK_SupervisorProductionEntryRevisionReview_Reviewer CHECK (LEN(TRIM(ReviewedBy)) > 0),
    CONSTRAINT CK_SupervisorProductionEntryRevisionReview_Utc CHECK (DATEPART(TZOFFSET, ReviewedAtUtc) = 0)
);
