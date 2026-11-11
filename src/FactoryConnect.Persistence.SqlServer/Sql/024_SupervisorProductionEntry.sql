-- Additive supervisor-declared production context. No CNC or metric publication is changed.
CREATE TABLE dbo.SupervisorProductionEntry
(
    EntryId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    ShiftScheduleAssignmentId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
    ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
    ProductionOrder nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Operation nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Note nvarchar(2000) COLLATE Latin1_General_100_BIN2 NULL,
    EnteredBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RecordedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorProductionEntry PRIMARY KEY (EntryId),
    CONSTRAINT CK_SupervisorProductionEntry_ShiftBounds CHECK
        (ShiftEndsAtUtc > ShiftStartsAtUtc),
    CONSTRAINT CK_SupervisorProductionEntry_ShiftUtc CHECK
        (DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0 AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0),
    CONSTRAINT CK_SupervisorProductionEntry_RecordedUtc CHECK
        (DATEPART(TZOFFSET, RecordedAtUtc) = 0),
    CONSTRAINT CK_SupervisorProductionEntry_RequiredText CHECK
        (LEN(TRIM(ShiftScheduleAssignmentId)) > 0 AND LEN(TRIM(ShiftId)) > 0
         AND LEN(TRIM(ProductionOrder)) > 0 AND LEN(TRIM(PartNumber)) > 0
         AND LEN(TRIM(Operation)) > 0 AND LEN(TRIM(EnteredBy)) > 0)
);
CREATE INDEX IX_SupervisorProductionEntry_MachineShift
ON dbo.SupervisorProductionEntry (MachineId, ShiftStartsAtUtc, ShiftEndsAtUtc, RecordedAtUtc);
