-- Separate, explicitly installed security authority. Never run by application startup.
IF OBJECT_ID('dbo.FactoryConnectSecurityState') IS NOT NULL
    THROW 51000, 'Security schema already exists; inspect its version instead of reinstalling.', 1;
CREATE TABLE dbo.FactoryConnectSecurityState (
    Id int NOT NULL CONSTRAINT PK_FactoryConnectSecurityState PRIMARY KEY,
    Version int NOT NULL,
    Revision bigint NOT NULL,
    Payload nvarchar(max) NOT NULL,
    CONSTRAINT CK_FactoryConnectSecurityState_Id CHECK (Id = 1),
    CONSTRAINT CK_FactoryConnectSecurityState_Version CHECK (Version = 1),
    CONSTRAINT CK_FactoryConnectSecurityState_Json CHECK (ISJSON(Payload) = 1)
);
CREATE TABLE dbo.FactoryConnectSecurityOperation (
    SubjectId uniqueidentifier NOT NULL,
    OperationId uniqueidentifier NOT NULL,
    Request varbinary(max) NOT NULL,
    AllocatedId uniqueidentifier NOT NULL CONSTRAINT DF_FactoryConnectSecurityOperation_AllocatedId DEFAULT NEWID(),
    Result nvarchar(max) NULL,
    RegisteredAt datetimeoffset(7) NOT NULL,
    CompletedAt datetimeoffset(7) NULL,
    CONSTRAINT PK_FactoryConnectSecurityOperation PRIMARY KEY (SubjectId, OperationId),
    CONSTRAINT CK_FactoryConnectSecurityOperation_Result CHECK (Result IS NULL OR ISJSON(Result) = 1)
);
CREATE TABLE dbo.FactoryConnectSecurityAudit (
    Sequence bigint IDENTITY NOT NULL CONSTRAINT PK_FactoryConnectSecurityAudit PRIMARY KEY,
    OccurredAt datetimeoffset(7) NOT NULL,
    SubjectId uniqueidentifier NULL,
    SessionId uniqueidentifier NULL,
    OperationId uniqueidentifier NULL,
    Action nvarchar(80) NOT NULL,
    MachineId uniqueidentifier NULL,
    AuthorizationRevision bigint NULL,
    DatabasePrincipal nvarchar(128) NOT NULL CONSTRAINT DF_FactoryConnectSecurityAudit_Principal DEFAULT ORIGINAL_LOGIN(),
    Outcome nvarchar(80) NOT NULL
);
