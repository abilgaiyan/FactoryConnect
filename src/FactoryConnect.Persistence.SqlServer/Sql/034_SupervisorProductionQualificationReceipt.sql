-- Durable qualification receipts are NOT supervisor approvals or accepted production.
-- No ingestion, qualification or publication writer is introduced by this migration.
-- Only an independently authorized service may create a Qualified receipt.
IF OBJECT_ID(N'dbo.SupervisorProductionQualificationReceipt', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupervisorProductionQualificationReceipt
    (
        ReceiptId uniqueidentifier NOT NULL,
        EvidenceId uniqueidentifier NOT NULL,
        EvidenceKind nvarchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
        PopulationKey nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        MachineId uniqueidentifier NOT NULL,
        ShiftScheduleAssignmentId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ShiftId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ShiftStartsAtUtc datetimeoffset(7) NOT NULL,
        ShiftEndsAtUtc datetimeoffset(7) NOT NULL,
        ProductionOrder nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        Operation nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        QuantityUnit nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ProducedQuantity bigint NOT NULL,
        GoodQuantity bigint NOT NULL,
        RejectedQuantity bigint NOT NULL,
        QualificationDecision nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
        AuthorityReference nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        QualifiedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT PK_SupervisorProductionQualificationReceipt PRIMARY KEY (ReceiptId),
        CONSTRAINT UQ_SupervisorProductionQualificationReceipt_Evidence UNIQUE (EvidenceKind, EvidenceId),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Identity CHECK
            (ReceiptId <> '00000000-0000-0000-0000-000000000000'
             AND EvidenceId <> '00000000-0000-0000-0000-000000000000'
             AND MachineId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Source CHECK
            (EvidenceKind IN (N'CncCounter', N'SupervisorRevision')),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Decision CHECK
            (QualificationDecision IN (N'Qualified', N'Rejected')),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Quantity CHECK
            (ProducedQuantity >= 0 AND GoodQuantity >= 0 AND RejectedQuantity >= 0
             AND GoodQuantity <= ProducedQuantity
             AND RejectedQuantity = ProducedQuantity - GoodQuantity),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Utc CHECK
            (DATEPART(TZOFFSET, ShiftStartsAtUtc) = 0
             AND DATEPART(TZOFFSET, ShiftEndsAtUtc) = 0
             AND DATEPART(TZOFFSET, QualifiedAtUtc) = 0),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Interval CHECK
            (ShiftEndsAtUtc > ShiftStartsAtUtc
             AND DATEDIFF(MINUTE, ShiftStartsAtUtc, ShiftEndsAtUtc) <= 1440),
        CONSTRAINT CK_SupervisorProductionQualificationReceipt_Required CHECK
            (LEN(TRIM(PopulationKey)) > 0 AND LEN(TRIM(ShiftScheduleAssignmentId)) > 0
             AND LEN(TRIM(ShiftId)) > 0 AND LEN(TRIM(QuantityUnit)) > 0
             AND LEN(TRIM(AuthorityReference)) > 0)
    );
    CREATE INDEX IX_SupervisorProductionQualificationReceipt_Shift
    ON dbo.SupervisorProductionQualificationReceipt
        (MachineId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc);
END;
