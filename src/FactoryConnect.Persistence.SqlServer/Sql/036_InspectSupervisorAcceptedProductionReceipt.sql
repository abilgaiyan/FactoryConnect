-- Read-only, fail-closed publication eligibility inspection.
-- Does not qualify evidence, assert complete coverage, or insert accepted production.
-- The transactional writer must revalidate under locks in the same transaction.
CREATE OR ALTER PROCEDURE dbo.InspectSupervisorAcceptedProductionReceipt
    @ReceiptId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SELECT
        r.ReceiptId,
        r.MachineId,
        r.ShiftScheduleAssignmentId,
        r.ShiftId,
        r.ShiftStartsAtUtc,
        r.ShiftEndsAtUtc,
        r.PopulationKey,
        r.EvidenceKind,
        r.EvidenceId,
        r.ProducedQuantity,
        r.GoodQuantity,
        r.RejectedQuantity,
        r.QuantityUnit,
        r.QualificationDecision,
        CASE
            WHEN r.ReceiptId IS NULL THEN N'ReceiptNotFound'
            WHEN r.QualificationDecision <> N'Qualified' THEN N'NotQualified'
            WHEN EXISTS (
                SELECT 1
                FROM dbo.SupervisorAcceptedShiftProductionSource AS s
                WHERE s.ReceiptId = r.ReceiptId
            ) THEN N'AlreadyPublished'
            WHEN EXISTS (
                SELECT 1
                FROM dbo.SupervisorAcceptedShiftProductionSource AS s
                INNER JOIN dbo.SupervisorProductionQualificationReceipt AS other
                    ON other.ReceiptId = s.ReceiptId
                WHERE other.MachineId = r.MachineId
                  AND other.PopulationKey = r.PopulationKey
            ) THEN N'PopulationAlreadyPublished'
            WHEN EXISTS (
                SELECT 1
                FROM dbo.SupervisorAcceptedShiftProduction AS p
                WHERE p.MachineId = r.MachineId
                  AND p.ShiftScheduleAssignmentId = r.ShiftScheduleAssignmentId
                  AND p.ShiftId = r.ShiftId
                  AND p.ShiftStartsAtUtc = r.ShiftStartsAtUtc
                  AND p.ShiftEndsAtUtc = r.ShiftEndsAtUtc
            ) THEN N'ShiftAlreadyPublished'
            ELSE N'RequiresCoverageAndAuthorityVerification'
        END AS InspectionStatus
    FROM (SELECT @ReceiptId AS RequestedReceiptId) AS requested
    LEFT JOIN dbo.SupervisorProductionQualificationReceipt AS r
        ON r.ReceiptId = requested.RequestedReceiptId;
END;
