-- Additive local supervisor quantity declarations; historical context-only entries remain NULL.
-- Declared quantities are not CNC observations, reconciled contributions, or OEE facts.
ALTER TABLE dbo.SupervisorProductionEntry
ADD GoodQuantity bigint NULL, RejectedQuantity bigint NULL;
EXEC(N'ALTER TABLE dbo.SupervisorProductionEntry
ADD CONSTRAINT CK_SupervisorProductionEntry_QuantityPair CHECK
(
    (GoodQuantity IS NULL AND RejectedQuantity IS NULL)
    OR (GoodQuantity IS NOT NULL AND RejectedQuantity IS NOT NULL
        AND GoodQuantity >= 0 AND RejectedQuantity >= 0)
)');
