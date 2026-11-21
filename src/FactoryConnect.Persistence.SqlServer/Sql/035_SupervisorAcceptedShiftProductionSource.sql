-- Publication ancestry is explicit: a qualified source receipt may contribute to at most
-- one accepted shift publication. This migration does not admit any evidence,
-- create qualification receipts, or publish production.
IF OBJECT_ID(N'dbo.SupervisorAcceptedShiftProductionSource', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupervisorAcceptedShiftProductionSource
    (
        PublicationId uniqueidentifier NOT NULL,
        ReceiptId uniqueidentifier NOT NULL,
        CONSTRAINT PK_SupervisorAcceptedShiftProductionSource
            PRIMARY KEY (PublicationId, ReceiptId),
        CONSTRAINT UQ_SupervisorAcceptedShiftProductionSource_Receipt
            UNIQUE (ReceiptId),
        CONSTRAINT FK_SupervisorAcceptedShiftProductionSource_Publication
            FOREIGN KEY (PublicationId)
            REFERENCES dbo.SupervisorAcceptedShiftProduction (PublicationId),
        CONSTRAINT FK_SupervisorAcceptedShiftProductionSource_Receipt
            FOREIGN KEY (ReceiptId)
            REFERENCES dbo.SupervisorProductionQualificationReceipt (ReceiptId)
    );
END;
