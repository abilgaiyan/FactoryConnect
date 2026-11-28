-- Preserve qualified receipt context in new publications; existing publications are unchanged.
EXEC(N'CREATE OR ALTER PROCEDURE dbo.PublishSupervisorAcceptedProduction
    @PublicationId uniqueidentifier,
    @ReceiptId uniqueidentifier,
    @VerificationId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @machine uniqueidentifier, @population nvarchar(200),
                @assignment nvarchar(200), @shift nvarchar(200),
                @starts datetimeoffset(7), @ends datetimeoffset(7),
                @unit nvarchar(50), @quantity bigint,
                @authority nvarchar(200), @order nvarchar(200), @part nvarchar(200), @operation nvarchar(200);

        SELECT @machine = r.MachineId, @population = r.PopulationKey,
               @assignment = r.ShiftScheduleAssignmentId, @shift = r.ShiftId,
               @starts = r.ShiftStartsAtUtc, @ends = r.ShiftEndsAtUtc,
               @unit = r.QuantityUnit, @quantity = r.ProducedQuantity,
               @authority = v.AuthorityReference, @order = r.ProductionOrder,
               @part = r.PartNumber, @operation = r.Operation
        FROM dbo.SupervisorProductionQualificationReceipt AS r WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.SupervisorAcceptedProductionVerification AS v WITH (UPDLOCK, HOLDLOCK)
          ON v.ReceiptId = r.ReceiptId AND v.VerificationId = @VerificationId
         AND v.VerifiedProducedQuantity = r.ProducedQuantity
         AND v.Coverage = N''Complete''
        WHERE r.ReceiptId = @ReceiptId AND r.QualificationDecision = N''Qualified'';

        IF @machine IS NULL
            THROW 51038, ''Qualified receipt and independent complete-coverage verification required.'', 1;

        IF EXISTS (SELECT 1 FROM dbo.SupervisorAcceptedShiftProductionSource WITH (UPDLOCK, HOLDLOCK)
                   WHERE ReceiptId = @ReceiptId)
            THROW 51039, ''Receipt already published.'', 1;

        IF EXISTS (
            SELECT 1 FROM dbo.SupervisorAcceptedShiftProductionSource AS s WITH (UPDLOCK, HOLDLOCK)
            JOIN dbo.SupervisorProductionQualificationReceipt AS r WITH (UPDLOCK, HOLDLOCK)
              ON r.ReceiptId = s.ReceiptId
            WHERE r.MachineId = @machine AND r.PopulationKey = @population)
            THROW 51040, ''Population already published.'', 1;

        IF EXISTS (
            SELECT 1 FROM dbo.SupervisorAcceptedShiftProduction WITH (UPDLOCK, HOLDLOCK)
            WHERE MachineId = @machine AND ShiftScheduleAssignmentId = @assignment
              AND ShiftId = @shift AND ShiftStartsAtUtc = @starts AND ShiftEndsAtUtc = @ends)
            THROW 51041, ''Shift already published.'', 1;

        INSERT dbo.SupervisorAcceptedShiftProduction
            (PublicationId, MachineId, ShiftScheduleAssignmentId, ShiftId,
             ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionOrder, PartNumber, Operation, QuantityUnit,
             AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
        VALUES (@PublicationId, @machine, @assignment, @shift, @starts, @ends, @order, @part, @operation,
                @unit, @quantity, N''Complete'', @authority, TODATETIMEOFFSET(SYSUTCDATETIME(), 0));

        INSERT dbo.SupervisorAcceptedShiftProductionSource (PublicationId, ReceiptId)
        VALUES (@PublicationId, @ReceiptId);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;');
