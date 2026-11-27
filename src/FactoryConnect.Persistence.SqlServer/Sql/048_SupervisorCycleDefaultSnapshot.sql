CREATE TABLE dbo.SupervisorCycleDefaultSnapshot
(
    SnapshotId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftScheduleAssignmentId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
    ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
    ProductionOrder nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
    PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Operation nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
    PartSeconds decimal(18,3) NOT NULL,
    OperationSeconds decimal(18,3) NULL,
    CapturedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorCycleDefaultSnapshot PRIMARY KEY (SnapshotId),
    CONSTRAINT CK_SupervisorCycleDefaultSnapshot_Part CHECK (PartSeconds > 0 AND PartSeconds <= 86400),
    CONSTRAINT CK_SupervisorCycleDefaultSnapshot_Operation CHECK (OperationSeconds IS NULL OR (Operation IS NOT NULL AND OperationSeconds > 0 AND OperationSeconds <= 86400)),
    CONSTRAINT CK_SupervisorCycleDefaultSnapshot_Utc CHECK (DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0 AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0 AND DATEPART(TZOFFSET, CapturedAtUtc) = 0),
    CONSTRAINT CK_SupervisorCycleDefaultSnapshot_Interval CHECK (ShiftEndsAtUtc > ShiftStartsAtUtc),
    CONSTRAINT CK_SupervisorCycleDefaultSnapshot_Keys CHECK (LEN(LTRIM(RTRIM(ShiftScheduleAssignmentId))) > 0 AND LEN(LTRIM(RTRIM(ShiftId))) > 0 AND LEN(LTRIM(RTRIM(PartNumber))) > 0)
);
