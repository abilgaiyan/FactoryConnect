# A2 Counter Quantity Authority

Status at implementation branch: **publication gated**.

This document records the frozen A2 boundary without enabling ProducedQuantity.

## A2.1 Counter-to-quantity contract

A positive raw counter difference is not, by itself, authoritative ProducedQuantity. Raw machine observations remain durable evidence and are never rewritten as quantity merely to satisfy reporting.

## A2.2.A Acquisition continuity publication

The provider-owned continuity model requires immutable stream publication ordering, recovery intent replay identity, barrier identity, predecessor linkage, and exact raw/contact publication binding. A pending or indeterminate continuity boundary cannot be crossed by a quantity pair. Empty successful acquisition publications may bind a barrier but do not manufacture raw quantity evidence.

## A2.3B Observation-accounting convention

For predecessor P and current observation C, an otherwise lifecycle-qualified increase is accounted over `(P,C]`. Qualification requires `P < C`, and one exact half-open production occurrence/context interval `[Start,End)` must contain the accounting interval with `Start <= P` and `C < End`. Quantity is never split across an attribution boundary. `OccurredAtUtc = C` is an accounting/recognition anchor, not a claim of physical completion time.

## A2.3A Lifecycle gate

Lifecycle authority remains externally unestablished for the deployed M01 COMPLETE counter. Until applicable machine/vendor/configuration authority excludes an unobserved reset/rewrite/lifecycle discontinuity for the exact pair, the lifecycle outcome cannot be `Qualified`.

Therefore this branch MUST NOT publish authoritative ProducedQuantity from M01 counter deltas. `GoodQuantity` and `RejectedQuantity` are separate facts and cannot repair this gate. No historical backfill or synthetic quality is permitted.

## Required outcome behavior

- `Qualified`: only possible when A2.2 acquisition coverage, A2.3A lifecycle authority, and A2.3B attribution authority are all established for the exact predecessor/current pair.
- `Break`: definitive break may reject/rebaseline with zero quantity evidence.
- `Indeterminate`: affected pair does not advance and emits no quantity evidence.
- Unchanged counter: emits no quantity.

The current factory release remains independent of this branch until a separately authorized deployment.
