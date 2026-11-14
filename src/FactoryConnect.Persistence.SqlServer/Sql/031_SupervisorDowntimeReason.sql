-- SUP-5: configurable reason codes for supervisor downtime explanations.
-- Catalog only: no rewrite of existing immutable explanation/review evidence.
IF OBJECT_ID(N'dbo.SupervisorDowntimeReason', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupervisorDowntimeReason
    (
        ReasonCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(1000) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_SupervisorDowntimeReason_IsActive DEFAULT (1),
        CreatedAtUtc datetimeoffset(7) NOT NULL,
        UpdatedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT PK_SupervisorDowntimeReason PRIMARY KEY (ReasonCode),
        CONSTRAINT CK_SupervisorDowntimeReason_Code CHECK (LEN(LTRIM(RTRIM(ReasonCode))) > 0),
        CONSTRAINT CK_SupervisorDowntimeReason_Name CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0),
        CONSTRAINT CK_SupervisorDowntimeReason_Utc CHECK
          (DATEPART(TZOFFSET, CreatedAtUtc) = 0 AND DATEPART(TZOFFSET, UpdatedAtUtc) = 0)
    );
END;
