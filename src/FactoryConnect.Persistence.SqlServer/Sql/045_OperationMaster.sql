CREATE TABLE dbo.OperationMaster
(
    PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OperationCode nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    DisplayName nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IdealCycleSeconds decimal(18,3) NULL,
    IsActive bit NOT NULL,
    UpdatedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_OperationMaster PRIMARY KEY (PartNumber, OperationCode),
    CONSTRAINT FK_OperationMaster_PartMaster FOREIGN KEY (PartNumber) REFERENCES dbo.PartMaster(PartNumber),
    CONSTRAINT CK_OperationMaster_Cycle CHECK (IdealCycleSeconds > 0 AND IdealCycleSeconds <= 86400),
    CONSTRAINT CK_OperationMaster_Required CHECK (LEN(LTRIM(RTRIM(OperationCode))) > 0 AND LEN(LTRIM(RTRIM(DisplayName))) > 0 AND LEN(LTRIM(RTRIM(UpdatedBy))) > 0),
    CONSTRAINT CK_OperationMaster_Utc CHECK (DATEPART(TZOFFSET, UpdatedAtUtc) = 0)
);
