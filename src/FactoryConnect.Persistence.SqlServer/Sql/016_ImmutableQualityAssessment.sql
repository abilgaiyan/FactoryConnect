CREATE TABLE dbo.QualityPolicy
(
    IdentityHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    IdentityBinary varbinary(max) NOT NULL,
    PayloadCodecVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_QualityPolicy PRIMARY KEY CLUSTERED (IdentityHash),
    CONSTRAINT CK_QualityPolicy_IdentityVersion CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_QualityPolicy_PayloadVersion CHECK (PayloadCodecVersion > 0),
    CONSTRAINT CK_QualityPolicy_IdentitySize CHECK (DATALENGTH(IdentityBinary) BETWEEN 1 AND 16777216),
    CONSTRAINT CK_QualityPolicy_PayloadSize CHECK (DATALENGTH(PayloadBinary) BETWEEN 1 AND 16777216)
);

CREATE TABLE dbo.QualityAssessment
(
    IdentityHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    IdentityBinary varbinary(max) NOT NULL,
    PayloadCodecVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_QualityAssessment PRIMARY KEY CLUSTERED (IdentityHash),
    CONSTRAINT CK_QualityAssessment_IdentityVersion CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_QualityAssessment_PayloadVersion CHECK (PayloadCodecVersion > 0),
    CONSTRAINT CK_QualityAssessment_IdentitySize CHECK (DATALENGTH(IdentityBinary) BETWEEN 1 AND 16777216),
    CONSTRAINT CK_QualityAssessment_PayloadSize CHECK (DATALENGTH(PayloadBinary) BETWEEN 1 AND 16777216)
);
