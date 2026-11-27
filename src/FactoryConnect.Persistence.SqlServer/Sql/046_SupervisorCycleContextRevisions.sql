-- Preserve every existing revision; only widen the uniqueness scope.
ALTER TABLE dbo.SupervisorShiftIdealCycle DROP CONSTRAINT UQ_SupervisorShiftIdealCycle_Revision;
ALTER TABLE dbo.SupervisorShiftIdealCycle ADD CONSTRAINT UQ_SupervisorShiftIdealCycle_Revision
    UNIQUE NONCLUSTERED (MachineId, ShiftScheduleAssignmentId, ShiftStartsAtUtc, ShiftEndsAtUtc,
                         ProductionOrder, PartNumber, Operation, RevisionNumber);
