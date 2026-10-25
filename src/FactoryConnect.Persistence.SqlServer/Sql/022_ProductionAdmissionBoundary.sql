ALTER TABLE dbo.ProductionContextProcessor ADD
    AdmissionBound bit NOT NULL CONSTRAINT DF_ProductionContextProcessor_AdmissionBound DEFAULT (0),
    ProductionStartsAt datetimeoffset(7) NULL,
    CONSTRAINT CK_ProductionContextProcessor_AdmissionBinding
        CHECK (AdmissionBound = 1 OR ProductionStartsAt IS NULL);
