CREATE TABLE dbo.AssumedBreakConfigurationSnapshot
(
    IdentityHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    IdentityBinary varbinary(max) NOT NULL,
    PayloadCodecVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_AssumedBreakConfigurationSnapshot PRIMARY KEY CLUSTERED (IdentityHash),
    CONSTRAINT CK_AssumedBreakConfigurationSnapshot_IdentityVersion CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_AssumedBreakConfigurationSnapshot_PayloadVersion CHECK (PayloadCodecVersion > 0),
    CONSTRAINT CK_AssumedBreakConfigurationSnapshot_IdentitySize CHECK (DATALENGTH(IdentityBinary) BETWEEN 1 AND 1048576),
    CONSTRAINT CK_AssumedBreakConfigurationSnapshot_PayloadSize CHECK (DATALENGTH(PayloadBinary) BETWEEN 1 AND 1048576)
);

CREATE TABLE dbo.AssumedBreakConfigurationAdmission
(
    IdentityHash binary(32) NOT NULL,
    SnapshotIdentityHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    IdentityBinary varbinary(max) NOT NULL,
    PayloadCodecVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT FK_AssumedBreakConfigurationAdmission_Snapshot FOREIGN KEY (SnapshotIdentityHash) REFERENCES dbo.AssumedBreakConfigurationSnapshot (IdentityHash),
    CONSTRAINT PK_AssumedBreakConfigurationAdmission PRIMARY KEY CLUSTERED (IdentityHash),
    CONSTRAINT CK_AssumedBreakConfigurationAdmission_IdentityVersion CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_AssumedBreakConfigurationAdmission_PayloadVersion CHECK (PayloadCodecVersion > 0),
    CONSTRAINT CK_AssumedBreakConfigurationAdmission_IdentitySize CHECK (DATALENGTH(IdentityBinary) BETWEEN 1 AND 1048576),
    CONSTRAINT CK_AssumedBreakConfigurationAdmission_PayloadSize CHECK (DATALENGTH(PayloadBinary) BETWEEN 1 AND 1048576)
);
CREATE INDEX IX_AssumedBreakConfigurationAdmission_Snapshot ON dbo.AssumedBreakConfigurationAdmission (SnapshotIdentityHash);

