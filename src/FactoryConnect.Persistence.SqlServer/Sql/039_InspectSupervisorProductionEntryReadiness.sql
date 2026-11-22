-- Read-only supervisor declaration qualification-readiness inspection.
-- Local review is NOT independent factory verification. No readiness state authorizes publication.
CREATE OR ALTER PROCEDURE dbo.InspectSupervisorProductionEntryReadiness
    @EntryId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    ;WITH CurrentRevision AS
    (
        SELECT revision.*, ROW_NUMBER() OVER
            (PARTITION BY revision.EntryId ORDER BY revision.RevisionNumber DESC) AS RowNumber
        FROM dbo.SupervisorProductionEntryRevision AS revision
        WHERE revision.EntryId = @EntryId
    )
    SELECT
        e.EntryId, e.MachineId, e.ShiftScheduleAssignmentId, e.ShiftId,
        e.ShiftStartsAtUtc, e.ShiftEndsAtUtc,
        CASE WHEN revision.RevisionId IS NULL THEN N'SupervisorEntry' ELSE N'SupervisorRevision' END AS SourceKind,
        COALESCE(revision.RevisionId, e.EntryId) AS CurrentEvidenceId,
        revision.RevisionNumber,
        COALESCE(revision.ProductionOrder, e.ProductionOrder) AS ProductionOrder,
        COALESCE(revision.PartNumber, e.PartNumber) AS PartNumber,
        COALESCE(revision.Operation, e.Operation) AS Operation,
        COALESCE(revision.GoodQuantity, e.GoodQuantity) AS GoodQuantity,
        COALESCE(revision.RejectedQuantity, e.RejectedQuantity) AS RejectedQuantity,
        CASE WHEN revision.RevisionId IS NULL THEN originalReview.Decision
             ELSE revisionReview.Decision END AS LocalReviewDecision,
        receipt.ReceiptId,
        receipt.QualificationDecision,
        verification.VerificationId,
        published.PublicationId,
        CASE
            WHEN e.EntryId IS NULL THEN N'EntryNotFound'
            WHEN revision.RevisionId IS NULL AND originalReview.Decision = N'Rejected'
                THEN N'LocalReviewRejected'
            WHEN revision.RevisionId IS NOT NULL AND revisionReview.Decision = N'Rejected'
                THEN N'CurrentRevisionRejected'
            WHEN revision.RevisionId IS NULL AND originalReview.Decision IS NULL
                THEN N'LocalReviewMissing'
            WHEN revision.RevisionId IS NOT NULL AND revisionReview.Decision IS NULL
                THEN N'CurrentRevisionReviewMissing'
            WHEN (CASE WHEN revision.RevisionId IS NULL THEN e.GoodQuantity ELSE revision.GoodQuantity END) IS NULL
              OR (CASE WHEN revision.RevisionId IS NULL THEN e.RejectedQuantity ELSE revision.RejectedQuantity END) IS NULL
                THEN N'QuantitiesMissing'
            WHEN receipt.ReceiptId IS NULL THEN N'QualificationReceiptMissing'
            WHEN receipt.QualificationDecision <> N'Qualified' THEN N'QualificationRejected'
            WHEN published.PublicationId IS NOT NULL THEN N'AlreadyPublished'
            WHEN verification.VerificationId IS NULL THEN N'IndependentVerificationMissing'
            ELSE N'CoverageAndPopulationReconciliationNotEstablished'
        END AS ReadinessStatus,
        CAST(0 AS bit) AS PublicationAuthorized
    FROM (SELECT @EntryId AS RequestedEntryId) AS requested
    LEFT JOIN dbo.SupervisorProductionEntry AS e ON e.EntryId = requested.RequestedEntryId
    LEFT JOIN CurrentRevision AS revision ON revision.EntryId = e.EntryId AND revision.RowNumber = 1
    LEFT JOIN dbo.SupervisorProductionEntryReview AS originalReview ON originalReview.EntryId = e.EntryId
    LEFT JOIN dbo.SupervisorProductionEntryRevisionReview AS revisionReview ON revisionReview.RevisionId = revision.RevisionId
    OUTER APPLY
    (
        SELECT TOP (1) r.ReceiptId, r.QualificationDecision
        FROM dbo.SupervisorProductionQualificationReceipt AS r
        WHERE r.EvidenceKind = N'SupervisorRevision'
          AND r.EvidenceId = revision.RevisionId
          AND r.MachineId = e.MachineId
          AND r.ShiftScheduleAssignmentId = e.ShiftScheduleAssignmentId
          AND r.ShiftId = e.ShiftId
          AND r.ShiftStartsAtUtc = e.ShiftStartsAtUtc
          AND r.ShiftEndsAtUtc = e.ShiftEndsAtUtc
        ORDER BY r.QualifiedAtUtc DESC, r.ReceiptId
    ) AS receipt
    LEFT JOIN dbo.SupervisorAcceptedProductionVerification AS verification
      ON verification.ReceiptId = receipt.ReceiptId
    LEFT JOIN dbo.SupervisorAcceptedShiftProductionSource AS publishedSource
      ON publishedSource.ReceiptId = receipt.ReceiptId
    LEFT JOIN dbo.SupervisorAcceptedShiftProduction AS published
      ON published.PublicationId = publishedSource.PublicationId;
END;
