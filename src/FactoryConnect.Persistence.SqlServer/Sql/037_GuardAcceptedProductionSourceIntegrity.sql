-- Fail closed when a source receipt is attached to an accepted-production publication.
-- This is an integrity guard, NOT evidence qualification or a publication writer.
-- Coverage and independent authority must still be verified before publishing.
CREATE OR ALTER TRIGGER dbo.TR_SupervisorAcceptedShiftProductionSource_Integrity
ON dbo.SupervisorAcceptedShiftProductionSource
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted AS i
        INNER JOIN dbo.SupervisorProductionQualificationReceipt AS r
            ON r.ReceiptId = i.ReceiptId
        INNER JOIN dbo.SupervisorAcceptedShiftProduction AS p
            ON p.PublicationId = i.PublicationId
        WHERE r.QualificationDecision <> N'Qualified'
           OR r.MachineId <> p.MachineId
           OR r.ShiftScheduleAssignmentId <> p.ShiftScheduleAssignmentId
           OR r.ShiftId <> p.ShiftId
           OR r.ShiftStartsAtUtc <> p.ShiftStartsAtUtc
           OR r.ShiftEndsAtUtc <> p.ShiftEndsAtUtc
           OR r.QuantityUnit <> p.QuantityUnit
    )
    BEGIN
        THROW 51037, 'Accepted production source must be qualified and match the publication machine, shift occurrence and quantity unit.', 1;
    END;
END;
