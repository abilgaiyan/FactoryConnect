ALTER TABLE dbo.OperationalMetricProjectionManifest
    DROP CONSTRAINT FK_OperationalMetricProjectionManifest_Checkpoint;

ALTER TABLE dbo.OperationalMetricProjectionManifest
    WITH CHECK ADD CONSTRAINT FK_OperationalMetricProjectionManifest_Processor
    FOREIGN KEY (OperationalMetricProjectionProcessorRowId)
    REFERENCES dbo.OperationalMetricProjectionProcessor
        (OperationalMetricProjectionProcessorRowId);
