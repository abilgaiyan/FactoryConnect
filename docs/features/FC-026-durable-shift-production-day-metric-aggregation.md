# FC-026 — Durable Shift and Production-Day Metric Aggregation

## Purpose

FC-026 converts the durable metric-input facts produced by FC-025 into durable additive totals by shift occurrence and production day. It does not reclassify production context and does not calculate presentation formulas such as OEE, utilization, incentives, or reporting labels.

## End-to-end boundary

```text
FC-025 activity processor      FC-025 quantity processor
          │                              │
          └──────── independent source checkpoints ────────┐
                                                         │
                         atomic producer publication       │
                                                         ↓
                  machine metric-input stream
                  + immutable temporal ownership
                  + persistence-owned position
                              ↓
                  machine aggregation processor
                              ↓
             atomic aggregation transaction
             ├── contribution idempotency
             ├── shift-occurrence projection
             ├── production-day projection
             └── aggregation checkpoint
```

FC-025 owns `ShiftOccurrenceId` and `ProductionDayId`. Persistence owns `MetricInputPosition`. FC-026 consumes those values exactly as persisted and never recomputes schedules, time zones, production-day boundaries, or calendar ownership.

## Durable input stream

Each machine has one `MetricInputStreamId`. Activity-derived and quantity-derived metric facts retain independent producing checkpoints but publish into the same machine-scoped stream. Persistence allocates monotonically increasing positions transactionally.

Replay rules are:

```text
new FactId + valid immutable payload
    → allocate one position

existing FactId + equivalent payload
    → preserve original position

existing FactId + conflicting payload
    → reject
```

Producer publication and producer checkpoint advancement are one durability boundary. A positioned fact is never visible unless its producing FC-025 checkpoint commits, and a producing checkpoint cannot acknowledge a fact that was not published.

## Aggregate identities

Shift aggregates are identified by machine, resolved shift occurrence, and metric-input key. Production-day aggregates are identified by machine, resolved production day, and metric-input key. `Unit` is deliberately excluded from aggregate identity.

```text
same aggregate key + same unit
    → merge

same aggregate key + incompatible unit
    → reject entire commit
```

The aggregate stores generic decimal value, unit, input count, first input timestamp, and last input timestamp.

## Aggregation transaction

For each durable read window the aggregation store performs one atomic commit:

```text
validate expected checkpoint
        ↓
verify durable positioned facts
        ↓
identify new/replayed contributions
        ↓
merge shift aggregates
        ↓
merge production-day aggregates
        ↓
record contribution identities
        ↓
advance checkpoint
        ↓
commit
```

If any step fails, neither projection, the contribution ledger, nor the checkpoint changes.

A new contribution must lie after the expected checkpoint and at or before the proposed checkpoint. An identical already-contributed fact may replay at or behind acknowledged progress as a no-op.

## Provider boundary

Persistence provider selection activates one coherent provider service set:

- `IObservationIngestionStore`
- `IProductionContextProcessingStore`
- `IMetricInputReader`
- `IMetricAggregationStore`

The neutral persistence assembly selects only by registered provider key. It contains no provider-name switch.

The in-memory provider shares one `InMemoryProductionContextProcessingStore` instance between FC-025 publication and downstream metric-input reading. The SQL Server provider uses one configured connection string across observation ingestion, FC-025 producer commits, metric-input reads, and aggregation storage.

SQL Server additionally provides executable additive migrations, relational ownership constraints, canonical decimal serialization, canonical aggregate keys with fixed-size lookup hashes, and full canonical-key verification after hash lookup.

## Machine execution and failure isolation

Edge creates one `MetricAggregationProcessingRuntime` per configured machine. The hosted worker supervises one independent polling loop per runtime.

A machine reader/store failure is logged for that processor and only that machine loop is delayed/retried. Other machine loops continue processing and advancing their own checkpoints. Cancellation is propagated to all loops and the worker joins all loops before stopping.

This operational isolation complements the durable identity isolation already provided by machine-scoped streams and processor-scoped checkpoints.

## Restart and replay

Each runtime restores its own durable aggregation checkpoint before its first read. The next request begins strictly after that position. Successful commit is the only acknowledgement boundary.

Therefore:

- restart resumes after the last committed position;
- identical replay cannot inflate totals;
- conflicting replay is rejected without progress;
- empty read windows may advance explicit source progress without creating aggregates.

## Edge composition

`MetricAggregation:BatchSize` and `MetricAggregation:PollingInterval` configure aggregation execution. Machine identity comes from the same machine-specific application configuration used to construct the machine acquisition/processing pipeline; FC-026 does not maintain a second independent machine inventory.

The composition root resolves `IMetricInputReader` and `IMetricAggregationStore` from the selected persistence provider and registers the metric aggregation hosted worker.

## FC-027 handoff

FC-026 ends at durable additive operational totals. The next reporting/evaluation feature may read shift and production-day aggregates to calculate formulas and reporting views. FC-026 remains intentionally unaware of OEE formulas, downtime labels, operators, jobs, incentives, dashboards, and historical correction orchestration.
