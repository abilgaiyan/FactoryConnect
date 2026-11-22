-- Machine-specific evidence capabilities. This table is configuration, not production evidence.
-- No machine is inferred from its name/controller and no default production counter is assigned.
-- Changes are append-only revisions so historical evidence is never silently reinterpreted.
IF OBJECT_ID(N'dbo.MachineProductionEvidenceProfile', N'U') IS NULL
BEGIN
CREATE TABLE dbo.MachineProductionEvidenceProfile
    (
        ProfileRevisionId uniqueidentifier NOT NULL
            CONSTRAINT PK_MachineProductionEvidenceProfile PRIMARY KEY,
        MachineId uniqueidentifier NOT NULL,
        RevisionNumber int NOT NULL,
        EvidenceMode nvarchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
        StateDataItemId nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        CounterDataItemId nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
        CounterSemantics nvarchar(30) COLLATE Latin1_General_100_BIN2 NULL,
        EffectiveFromUtc datetimeoffset(7) NOT NULL,
        ConfiguredBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ConfiguredAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT UQ_MachineProductionEvidenceProfile_Revision UNIQUE (MachineId, RevisionNumber),
        CONSTRAINT UQ_MachineProductionEvidenceProfile_Effective UNIQUE (MachineId, EffectiveFromUtc),
        CONSTRAINT CK_MachineProductionEvidenceProfile_Identity CHECK
            (MachineId <> '00000000-0000-0000-0000-000000000000'
             AND ProfileRevisionId <> '00000000-0000-0000-0000-000000000000'
             AND RevisionNumber > 0),
        CONSTRAINT CK_MachineProductionEvidenceProfile_Mode CHECK
            (EvidenceMode IN (N'CncCounterAndState', N'StateOnly', N'CounterOnly')),
        CONSTRAINT CK_MachineProductionEvidenceProfile_Signals CHECK
            ((EvidenceMode = N'StateOnly' AND StateDataItemId IS NOT NULL
              AND CounterDataItemId IS NULL AND CounterSemantics IS NULL)
             OR (EvidenceMode = N'CounterOnly' AND StateDataItemId IS NULL
              AND CounterDataItemId IS NOT NULL AND CounterSemantics IS NOT NULL)
             OR (EvidenceMode = N'CncCounterAndState' AND StateDataItemId IS NOT NULL
              AND CounterDataItemId IS NOT NULL AND CounterSemantics IS NOT NULL)),
        CONSTRAINT CK_MachineProductionEvidenceProfile_Counter CHECK
            (CounterSemantics IS NULL OR CounterSemantics IN
                (N'CumulativeResettable', N'CumulativeMonotonic', N'CyclePulse')),
        CONSTRAINT CK_MachineProductionEvidenceProfile_Required CHECK
            (LEN(TRIM(ConfiguredBy)) > 0
             AND (StateDataItemId IS NULL OR LEN(TRIM(StateDataItemId)) > 0)
             AND (CounterDataItemId IS NULL OR LEN(TRIM(CounterDataItemId)) > 0)),
        CONSTRAINT CK_MachineProductionEvidenceProfile_Utc CHECK
            (DATEPART(TZOFFSET, EffectiveFromUtc) = 0
             AND DATEPART(TZOFFSET, ConfiguredAtUtc) = 0)
    );
END;
-- No automatic migration from machine identity or raw state into quantity.
-- Profile configuration does not qualify, reconcile, verify or publish production.
