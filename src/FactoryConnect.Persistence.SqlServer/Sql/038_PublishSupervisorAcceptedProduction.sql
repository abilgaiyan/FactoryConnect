-- Transactional accepted-production writer. No API exposes this operation.
-- An independently issued verification is required; no issuance path is created here.
IF OBJECT_ID(N'dbo.SupervisorAcceptedProductionVerification', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupervisorAcceptedProductionVerification
    (
        VerificationId uniqueidentifier NOT NULL PRIMARY KEY,
        ReceiptId uniqueidentifier NOT NULL UNIQUE,
        VerifiedProducedQuantity bigint NOT NULL,
        Coverage nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
        AuthorityReference nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        VerifiedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT FK_SAPV_Receipt FOREIGN KEY (ReceiptId)
            REFERENCES dbo.SupervisorProductionQualificationReceipt (ReceiptId),
        CONSTRAINT CK_SAPV_Quantity CHECK (VerifiedProducedQuantity >= 0),
        CONSTRAINT CK_SAPV_Coverage CHECK (Coverage = N'Complete'),
        CONSTRAINT CK_SAPV_Authority CHECK (LEN(TRIM(AuthorityReference)) > 0),
        CONSTRAINT CK_SAPV_Utc CHECK (DATEPART(TZOFFSET, VerifiedAtUtc) = 0)
    );
END;
-- A verified record is a durable prerequisite, not self-attested caller input.
-- Until an independent verification issuer is implemented, production publication remains held.
-- The procedure body is DDL text; transaction statements execute only when the procedure is invoked.
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
                @authority nvarchar(200);

        SELECT @machine = r.MachineId, @population = r.PopulationKey,
               @assignment = r.ShiftScheduleAssignmentId, @shift = r.ShiftId,
               @starts = r.ShiftStartsAtUtc, @ends = r.ShiftEndsAtUtc,
               @unit = r.QuantityUnit, @quantity = r.ProducedQuantity,
               @authority = v.AuthorityReference
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
             ShiftStartsAtUtc, ShiftEndsAtUtc, QuantityUnit,
             AcceptedProducedQuantity, Coverage, AuthorityReference, PublishedAtUtc)
        VALUES (@PublicationId, @machine, @assignment, @shift, @starts, @ends,
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
