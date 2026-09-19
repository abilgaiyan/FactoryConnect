IF EXISTS (SELECT 1 FROM dbo.MachineStateChangeHistory)
    THROW 51009, 'Migration 009 requires dbo.MachineStateChangeHistory to be empty because Migration 008 did not persist durable InstanceId and Sequence.', 1;

IF EXISTS (SELECT 1 FROM dbo.MachineActivityPeriodHistory)
    THROW 51009, 'Migration 009 requires dbo.MachineActivityPeriodHistory to be empty because Migration 008 did not persist durable InstanceId and Sequence.', 1;

ALTER TABLE dbo.MachineStateChangeHistory
ADD InstanceId decimal(20,0) NOT NULL,
    Sequence decimal(20,0) NOT NULL;

EXEC(N'
ALTER TABLE dbo.MachineStateChangeHistory
ADD CONSTRAINT CK_MachineStateChangeHistory_InstanceId_UInt64
        CHECK (InstanceId BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_MachineStateChangeHistory_Sequence_UInt64
        CHECK (Sequence BETWEEN 0 AND 18446744073709551615);
');

ALTER TABLE dbo.MachineActivityPeriodHistory
ADD InstanceId decimal(20,0) NOT NULL,
    Sequence decimal(20,0) NOT NULL;

EXEC(N'
ALTER TABLE dbo.MachineActivityPeriodHistory
ADD CONSTRAINT CK_MachineActivityPeriodHistory_InstanceId_UInt64
        CHECK (InstanceId BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_MachineActivityPeriodHistory_Sequence_UInt64
        CHECK (Sequence BETWEEN 0 AND 18446744073709551615);
');
