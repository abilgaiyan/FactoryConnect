-- Local supervisor ideal cycle declarations; not qualified reference-time or OEE evidence.
IF OBJECT_ID(N'dbo.SupervisorShiftIdealCycle', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupervisorShiftIdealCycle
    (
        CycleRevisionId uniqueidentifier NOT NULL,
        MachineId uniqueidentifier NOT NULL,
        ShiftScheduleAssignmentId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ShiftId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
        ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
        RevisionNumber int NOT NULL,
        IdealCycleSeconds decimal(18,3) NOT NULL,
        ProductionOrder nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        Operation nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        RevisionReason nvarchar(2000) COLLATE Latin1_General_100_BIN2 NULL,
        EnteredBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RecordedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT PK_SupervisorShiftIdealCycle PRIMARY KEY (CycleRevisionId),
        CONSTRAINT UQ_SupervisorShiftIdealCycle_Revision UNIQUE
            (MachineId, ShiftScheduleAssignmentId, ShiftStartsAtUtc, ShiftEndsAtUtc, RevisionNumber),
        CONSTRAINT CK_SupervisorShiftIdealCycle_Quantity CHECK (IdealCycleSeconds > 0 AND IdealCycleSeconds <= 86400),
        CONSTRAINT CK_SupervisorShiftIdealCycle_Revision CHECK (RevisionNumber >= 1),
        CONSTRAINT CK_SupervisorShiftIdealCycle_RevisionReason CHECK
            (RevisionNumber = 1 OR
             (RevisionReason IS NOT NULL AND LEN(LTRIM(RTRIM(RevisionReason))) > 0)),
        CONSTRAINT CK_SupervisorShiftIdealCycle_Interval CHECK
            (ShiftEndsAtUtc > ShiftStartsAtUtc AND DATEDIFF(MINUTE, ShiftStartsAtUtc, ShiftEndsAtUtc) <= 1440),
        CONSTRAINT CK_SupervisorShiftIdealCycle_Utc CHECK
            (DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0 AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
             AND DATEPART(TZOFFSET, RecordedAtUtc) = 0),
        CONSTRAINT CK_SupervisorShiftIdealCycle_Required CHECK
            (LEN(LTRIM(RTRIM(ShiftScheduleAssignmentId))) > 0
             AND LEN(LTRIM(RTRIM(ShiftId))) > 0
             AND LEN(LTRIM(RTRIM(EnteredBy))) > 0)
    );
END;
