CREATE TABLE dbo.LocalUser
(
    UserId uniqueidentifier NOT NULL,
    Username nvarchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    PasswordSalt varbinary(32) NOT NULL,
    PasswordHash varbinary(32) NOT NULL,
    Iterations int NOT NULL,
    RoleName nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IsActive bit NOT NULL,
    SessionVersion uniqueidentifier NOT NULL CONSTRAINT DF_LocalUser_SessionVersion DEFAULT (NEWID()),
    FailedAttempts int NOT NULL CONSTRAINT DF_LocalUser_FailedAttempts DEFAULT (0),
    LockedUntilUtc datetime2(7) NULL,
    CONSTRAINT PK_LocalUser PRIMARY KEY CLUSTERED (UserId),
    CONSTRAINT UQ_LocalUser_Username UNIQUE NONCLUSTERED (Username),
    CONSTRAINT CK_LocalUser_Role CHECK (RoleName IN (N'Administrator', N'Supervisor', N'Manager')),
    CONSTRAINT CK_LocalUser_Iterations CHECK (Iterations >= 100000 AND Iterations <= 1000000),
    CONSTRAINT CK_LocalUser_Credentials CHECK (DATALENGTH(PasswordSalt) = 32 AND DATALENGTH(PasswordHash) = 32),
    CONSTRAINT CK_LocalUser_FailedAttempts CHECK (FailedAttempts >= 0 AND FailedAttempts <= 4)
);
