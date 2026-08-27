SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.MetricInputFact
        DROP CONSTRAINT FK_MetricInputFact_MetricInputStream;

    ALTER TABLE dbo.MetricInputStream
        ADD CONSTRAINT UQ_MetricInputStream_RowMachine
            UNIQUE (
                MetricInputStreamRowId,
                MachineId
            );

    ALTER TABLE dbo.MetricInputFact WITH CHECK
        ADD CONSTRAINT FK_MetricInputFact_StreamMachine
            FOREIGN KEY (
                MetricInputStreamRowId,
                MachineId
            )
            REFERENCES dbo.MetricInputStream (
                MetricInputStreamRowId,
                MachineId
            );

    ALTER TABLE dbo.MetricInputFact
        CHECK CONSTRAINT FK_MetricInputFact_StreamMachine;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
