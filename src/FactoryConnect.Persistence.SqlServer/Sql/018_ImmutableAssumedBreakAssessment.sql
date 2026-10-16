CREATE TABLE dbo.AssumedBreakAssessment
(
    IdentityHash binary(32) NOT NULL,
    IdentityCodecVersion int NOT NULL,
    IdentityBinary varbinary(max) NOT NULL,
    PayloadCodecVersion int NOT NULL,
    ContentHash binary(32) NOT NULL,
    PayloadBinary varbinary(max) NOT NULL,
    CONSTRAINT PK_AssumedBreakAssessment PRIMARY KEY CLUSTERED (IdentityHash),
    CONSTRAINT CK_AssumedBreakAssessment_IdentityVersion CHECK (IdentityCodecVersion > 0),
    CONSTRAINT CK_AssumedBreakAssessment_PayloadVersion CHECK (PayloadCodecVersion > 0),
    CONSTRAINT CK_AssumedBreakAssessment_IdentitySize CHECK (DATALENGTH(IdentityBinary) BETWEEN 1 AND 1048576),
    CONSTRAINT CK_AssumedBreakAssessment_PayloadSize CHECK (DATALENGTH(PayloadBinary) BETWEEN 1 AND 1048576)
);
