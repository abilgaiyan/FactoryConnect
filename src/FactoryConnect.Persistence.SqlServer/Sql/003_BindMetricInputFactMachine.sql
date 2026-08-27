ALTER TABLE dbo.MetricInputFact
    DROP CONSTRAINT FK_MetricInputFact_MetricInputStream;

ALTER TABLE dbo.MetricInputStream
    ADD CONSTRAINT UQ_MetricInputStream_RowMachine
        UNIQUE (
            MetricInputStreamRowId,
            MachineId
        );

ALTER TABLE dbo.MetricInputFact
    ADD CONSTRAINT FK_MetricInputFact_StreamMachine
        FOREIGN KEY (
            MetricInputStreamRowId,
            MachineId
        )
        REFERENCES dbo.MetricInputStream (
            MetricInputStreamRowId,
            MachineId
        );
