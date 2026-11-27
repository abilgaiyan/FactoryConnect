CREATE TABLE dbo.SupervisorCycleReview
(
    ReviewId uniqueidentifier NOT NULL,
    CycleRevisionId uniqueidentifier NOT NULL,
    IsAccepted bit NOT NULL,
    ReviewedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ReviewedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorCycleReview PRIMARY KEY (ReviewId),
    CONSTRAINT UQ_SupervisorCycleReview_Revision UNIQUE (CycleRevisionId),
    CONSTRAINT FK_SupervisorCycleReview_Revision FOREIGN KEY (CycleRevisionId)
        REFERENCES dbo.SupervisorShiftIdealCycle (CycleRevisionId),
    CONSTRAINT CK_SupervisorCycleReview_Actor CHECK (LEN(LTRIM(RTRIM(ReviewedBy))) > 0),
    CONSTRAINT CK_SupervisorCycleReview_Utc CHECK (DATEPART(TZOFFSET, ReviewedAtUtc) = 0)
);
