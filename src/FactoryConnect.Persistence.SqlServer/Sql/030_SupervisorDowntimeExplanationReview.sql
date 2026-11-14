-- Independent, immutable local review of a specific downtime explanation.
-- No machine activity or OEE publication is performed by this migration.
CREATE TABLE dbo.SupervisorDowntimeExplanationReview
(
    ExplanationId uniqueidentifier NOT NULL,
    DecisionId uniqueidentifier NOT NULL,
    Decision nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Reason nvarchar(2000) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorDowntimeExplanationReview PRIMARY KEY (ExplanationId),
    CONSTRAINT UQ_SupervisorDowntimeExplanationReview_DecisionId UNIQUE (DecisionId),
    CONSTRAINT FK_SupervisorDowntimeExplanationReview_Explanation
        FOREIGN KEY (ExplanationId)
        REFERENCES dbo.SupervisorDowntimeExplanation (ExplanationId),
    CONSTRAINT CK_SupervisorDowntimeExplanationReview_Decision
        CHECK (Decision IN (N'Approved', N'Rejected')),
    CONSTRAINT CK_SupervisorDowntimeExplanationReview_Required
        CHECK (LEN(TRIM(Reason)) > 0 AND LEN(TRIM(ReviewedBy)) > 0),
    CONSTRAINT CK_SupervisorDowntimeExplanationReview_Utc
        CHECK (DATEPART(TZOFFSET, ReviewedAtUtc) = 0)
);
