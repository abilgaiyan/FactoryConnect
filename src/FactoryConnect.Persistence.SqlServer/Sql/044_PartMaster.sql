CREATE TABLE dbo.PartMaster
(
    PartNumber nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT PK_PartMaster PRIMARY KEY,
    DisplayName nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Description nvarchar(1000) COLLATE Latin1_General_100_BIN2 NULL,
    IdealCycleSeconds decimal(18,3) NOT NULL,
    IsActive bit NOT NULL,
    UpdatedBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT CK_PartMaster_Cycle CHECK (IdealCycleSeconds > 0 AND IdealCycleSeconds <= 86400),
    CONSTRAINT CK_PartMaster_Required CHECK (LEN(LTRIM(RTRIM(PartNumber))) > 0 AND LEN(LTRIM(RTRIM(DisplayName))) > 0 AND LEN(LTRIM(RTRIM(UpdatedBy))) > 0),
    CONSTRAINT CK_PartMaster_Utc CHECK (DATEPART(TZOFFSET, UpdatedAtUtc) = 0)
);
