CREATE TABLE dbo.AcquisitionEvidencePublication
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    IdentityHash binary(32) NOT NULL,
    PublicationOrdinal bigint NOT NULL,
    PreviousIdentity binary(32) NULL,
    AcceptedBefore decimal(20,0) NULL,
    AcceptedThrough decimal(20,0) NULL,
    PayloadVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_AcquisitionEvidencePublication PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary, IdentityHash),
    CONSTRAINT FK_AcquisitionEvidencePublication_Stream FOREIGN KEY (MachineId, StreamKeyBinary)
        REFERENCES dbo.ObservationStreamCheckpoint (MachineId, StreamKeyBinary),
    CONSTRAINT FK_AcquisitionEvidencePublication_Previous FOREIGN KEY (MachineId, StreamKeyBinary, PreviousIdentity)
        REFERENCES dbo.AcquisitionEvidencePublication (MachineId, StreamKeyBinary, IdentityHash),
    CONSTRAINT CK_AcquisitionEvidencePublication_Ordinal CHECK (PublicationOrdinal > 0),
    CONSTRAINT CK_AcquisitionEvidencePublication_Version CHECK (PayloadVersion > 0),
    CONSTRAINT CK_AcquisitionEvidencePublication_Size CHECK (DATALENGTH(PayloadBinary) >= 1 AND DATALENGTH(PayloadBinary) <= 16777216)
);
CREATE UNIQUE NONCLUSTERED INDEX UX_AcquisitionEvidencePublication_Ordinal
ON dbo.AcquisitionEvidencePublication (MachineId, StreamKeyBinary, PublicationOrdinal);

CREATE TABLE dbo.AcquisitionEvidenceHead
(
    MachineId uniqueidentifier NOT NULL,
    StreamKeyBinary varbinary(512) NOT NULL,
    IdentityHash binary(32) NOT NULL,
    CONSTRAINT PK_AcquisitionEvidenceHead PRIMARY KEY CLUSTERED (MachineId, StreamKeyBinary),
    CONSTRAINT FK_AcquisitionEvidenceHead_Publication FOREIGN KEY (MachineId, StreamKeyBinary, IdentityHash)
        REFERENCES dbo.AcquisitionEvidencePublication (MachineId, StreamKeyBinary, IdentityHash)
);
