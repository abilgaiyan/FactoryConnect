# A2 Counter Quantity Authority

Status: **publication gated**.

## A2.1
A positive raw counter difference is not, by itself, authoritative ProducedQuantity. Raw machine observations remain durable evidence.

## A2.2.A
The provider-owned continuity model requires immutable stream publication ordering, recovery-intent replay identity, barrier identity, predecessor linkage, and exact raw/contact publication binding. Pending or indeterminate continuity cannot be crossed by a quantity pair. Empty successful acquisition publications may bind a barrier but do not manufacture raw quantity evidence.

## A2.3B
For predecessor P and current observation C, an otherwise lifecycle-qualified increase is accounted over `(P,C]`. Qualification requires `P < C`; one exact half-open production occurrence/context interval `[Start,End)` must contain it with `Start <= P` and `C < End`. Quantity is never split across an attribution boundary. `OccurredAtUtc = C` is an accounting/recognition anchor, not physical completion time.

## A2.3A external gate
Lifecycle authority remains unestablished for deployed M01 COMPLETE. Until applicable machine/vendor/configuration authority excludes an unobserved reset, rewrite, or lifecycle discontinuity for the exact pair, lifecycle cannot be `Qualified`.

Therefore implementation MUST NOT publish authoritative ProducedQuantity from M01 counter deltas. GoodQuantity and RejectedQuantity are separate facts and cannot repair this gate. No historical backfill or synthetic quality is permitted.

Outcome behavior: `Qualified` requires A2.2 coverage + A2.3A lifecycle + A2.3B attribution for the exact pair; definitive `Break` may reject/rebaseline with zero quantity evidence; `Indeterminate` does not advance the affected pair and emits nothing; unchanged counter emits nothing.
