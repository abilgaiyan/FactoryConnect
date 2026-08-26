# FC-025 — Durable Production Context and Metric Input Derivation

## Purpose

FC-025 interprets durable machine activity within historically correct factory operations and produces durable metric-input facts.

The feature does not calculate final KPIs and does not classify downtime reasons.

## Durable flow

```text
DurableMachineActivityPeriod
        +
Effective-dated ProductionContextAssignment
        +
Resolved ShiftOccurrence
        ↓
ContextualizedActivityInterval
        +
Resolved PlannedProductionInterval
        ↓
ProductionTimeEligibilityInterval
        ↓
DurableMetricInputFact
```

## Temporal invariants

- Production context is resolved for the historical activity interval, never from current configuration.
- Effective intervals and allocated intervals use half-open semantics: `[start, end)`.
- Shift definitions are recurring local configuration; `ShiftOccurrence` is an absolute UTC interval.
- Line-specific schedules override site-wide schedules for the requested line and date.
- Planned-production replacement overrides may activate an otherwise inactive recurring day.
- Allocation preserves source duration and cannot introduce gaps, overlaps, or zero-length fragments.
- Missing production context does not discard machine activity.

## Shift resolution

Shift count and duration are configuration, not application assumptions. A site may use two, three, four, or any other non-overlapping set of recurring shifts.

DST resolution is deterministic:

- an invalid spring-forward boundary advances to the first valid local instant;
- an ambiguous shift start selects the earlier UTC instant;
- an ambiguous shift end selects the later UTC instant.

Resolved duration therefore represents actual elapsed time.

## Planned production eligibility

A shift describes when factory operations exist. Planned-production intervals describe which portions of operational time are eligible as planned production time.

Planned breaks and shutdowns can remove eligibility. Calendar replacement windows can replace recurring planned windows for a specific factory date.

`IsPlannedProductionTime` is an eligibility fact only. It does not classify a stopped interval as planned or unplanned downtime.

## Metric input facts

FC-025 derives durable evidence facts such as:

- scheduled duration;
- planned-production duration;
- running duration;
- idle duration;
- stopped duration;
- alarm duration;
- offline duration when `MachineState.Offline` is explicit;
- part-count increments;
- good quantity;
- rejected quantity.

Facts are not percentages or final metrics. Availability, performance, quality, OEE, ELR, and similar calculations remain downstream.

Quantity facts require explicit quantity evidence. Running time never implies a produced quantity.

## Durable runtime composition

`ProductionContextProcessingRuntime` owns an independent `ObservationProcessorId` and checkpoint for one configured machine/stream scope.

A cycle performs:

```text
restore FC-025 checkpoint
        ↓
read durable activity after checkpoint
        ↓
resolve historical production context
resolve shift occurrences
        ↓
allocate contextualized activity
        ↓
resolve planned-production intervals
allocate planned/non-planned eligibility
        ↓
derive durable metric-input facts
        ↓
atomic commit
    contextualized intervals
    eligibility intervals
    metric facts
    next checkpoint
```

The checkpoint is not advanced until all outputs cross the same durable commit boundary.

## Provider-neutral contracts

FC-025 uses provider-neutral boundaries:

- `IProductionContextReader`
- `IShiftScheduleReader`
- `IPlannedProductionScheduleReader`
- `IProductionContextActivityReader`
- `IProductionContextProcessingStore`

The in-memory implementations are reference/conformance providers. SQL persistence for FC-025 outputs is outside this feature slice.

## Restart and replay

Output identities are deterministic. A restarted runtime restores its own checkpoint and resumes after the last committed durable activity position.

The in-memory conformance scenario proves:

- multi-batch processing;
- restart/resume without duplicate outputs;
- independent machine and line scopes;
- missing-context survival;
- atomic failure behavior at the durable commit boundary.

## Scope boundaries

FC-025 does not include:

- final KPI aggregation;
- OEE or utilization percentage calculation;
- downtime reason classification;
- reporting queries;
- SQL persistence for FC-025 projection outputs;
- production master-data editing workflows;
- UI or authorization.
