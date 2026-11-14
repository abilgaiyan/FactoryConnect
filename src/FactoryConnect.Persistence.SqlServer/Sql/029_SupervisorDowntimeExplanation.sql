-- Local supervisor downtime explanation, linked to a durable machine activity period.
-- Explanation does not change activity, machine state, or OEE.
CREATE TABLE dbo.SupervisorDowntimeExplanation
(
    ExplanationId uniqueidentifier NOT NULL,
    MachineId uniqueidentifier NOT NULL,
    StateProcessorId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ObservationPosition bigint NOT NULL,
    ActivityStartsAtUtc datetimeoffset(7) NOT NULL,
    ActivityEndsAtUtc datetimeoffset(7) NOT NULL,
    ReasonCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Explanation nvarchar(2000) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EnteredBy nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EnteredAtUtc datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_SupervisorDowntimeExplanation PRIMARY KEY (ExplanationId),
    CONSTRAINT UQ_SupervisorDowntimeExplanation_Period UNIQUE (MachineId, StateProcessorId, ObservationPosition),
    CONSTRAINT CK_SupervisorDowntimeExplanation_Position CHECK (ObservationPosition >= 0),
    CONSTRAINT CK_SupervisorDowntimeExplanation_Interval CHECK (ActivityStartsAtUtc < ActivityEndsAtUtc),
    CONSTRAINT CK_SupervisorDowntimeExplanation_Required CHECK
        (LEN(TRIM(StateProcessorId)) > 0 AND LEN(TRIM(ReasonCode)) > 0
         AND LEN(TRIM(Explanation)) > 0 AND LEN(TRIM(EnteredBy)) > 0),
    CONSTRAINT CK_SupervisorDowntimeExplanation_Utc CHECK
        (DATEPART(TZOFFSET, ActivityStartsAtUtc) = 0
         AND DATEPART(TZOFFSET, ActivityEndsAtUtc) = 0
         AND DATEPART(TZOFFSET, EnteredAtUtc) = 0)
);
