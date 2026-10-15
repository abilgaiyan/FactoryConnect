CREATE TABLE dbo.DerivedOeeAssessment
(
    IdentityHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    IdentityBinary varbinary(max) NOT NULL,
    PayloadCodecVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_DerivedOeeAssessment PRIMARY KEY CLUSTERED (IdentityHash),
    CONSTRAINT CK_DerivedOeeAssessment_IdentityVersion CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_DerivedOeeAssessment_PayloadVersion CHECK (PayloadCodecVersion > 0),
    CONSTRAINT CK_DerivedOeeAssessment_IdentitySize CHECK (DATALENGTH(IdentityBinary) BETWEEN 1 AND 16777216),
    CONSTRAINT CK_DerivedOeeAssessment_PayloadSize CHECK (DATALENGTH(PayloadBinary) BETWEEN 1 AND 16777216)
);
