-- Read-side publication projection only. No supervisor declaration or historical
-- metric input is automatically admitted. An independent qualification,
-- reconciliation and publication writer is required before rows can exist.
IF OBJECT_ID(N'dbo.SupervisorAcceptedShiftProduction', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupervisorAcceptedShiftProduction
    (
        PublicationId uniqueidentifier NOT NULL,
        MachineId uniqueidentifier NOT NULL,
        ShiftScheduleAssignmentId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ShiftId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
        ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
        ProductionOrder nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        Operation nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        QuantityUnit nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
        AcceptedProducedQuantity bigint NOT NULL,
        Coverage nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
        AuthorityReference nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        PublishedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT PK_SupervisorAcceptedShiftProduction PRIMARY KEY (PublicationId),
        CONSTRAINT UQ_SupervisorAcceptedShiftProduction_Occurrence UNIQUE
            (MachineId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc),
        CONSTRAINT CK_SupervisorAcceptedShiftProduction_Quantity CHECK (AcceptedProducedQuantity >= 0),
        CONSTRAINT CK_SupervisorAcceptedShiftProduction_Coverage CHECK (Coverage = N'Complete'),
        CONSTRAINT CK_SupervisorAcceptedShiftProduction_Utc CHECK
            (DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0 AND
             DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0 AND
             DATEPART(TZOFFSET, PublishedAtUtc) = 0),
        CONSTRAINT CK_SupervisorAcceptedShiftProduction_Interval CHECK
            (ShiftEndsAtUtc > ShiftStartsAtUtc AND
             DATEDIFF(MINUTE, ShiftStartsAtUtc, ShiftEndsAtUtc) <= 1440),
        CONSTRAINT CK_SupervisorAcceptedShiftProduction_Required CHECK
            (LEN(LTRIM(RTRIM(ShiftScheduleAssignmentId))) > 0 AND
             LEN(LTRIM(RTRIM(ShiftId))) > 0 AND
             LEN(LTRIM(RTRIM(QuantityUnit))) > 0 AND
             LEN(LTRIM(RTRIM(AuthorityReference))) > 0)
    );
END;
