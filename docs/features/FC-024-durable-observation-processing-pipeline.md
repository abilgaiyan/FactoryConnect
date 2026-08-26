# FC-024 — Durable Observation Processing Pipeline

## Purpose

FC-024 transforms acquired machine observations into durable, replay-safe
factory-domain history without coupling the domain runtime to a persistence
provider.

## Transformation chain

```text
Protocol observation
    ↓
Atomic raw ingestion and acquisition checkpoint
    ↓
Durable raw observation position
    ↓
Canonical signal mapping
    ↓
Durable canonical observation
    ↓
Machine state/activity projection
    ├── durable state change
    └── durable completed activity period
```

Each stage owns independent progress. A downstream failure never moves the
upstream processing checkpoint for that stage.

## Durable identities

Raw and canonical observations preserve:

- observation stream
- durable observation position
- protocol instance
- protocol sequence

State changes and completed activity periods additionally preserve the stable
processor identity. A completed activity period uses the position of the
observation that closes the period:

```text
Position  = closing observation identity
StartedAt = activity start timestamp
EndedAt   = closing transition timestamp
```

## Processing semantics

Processing does not imply output:

```text
raw processed + no mapping
    → no canonical observation
    → raw processing checkpoint advances

canonical processed + unchanged state
    → no state change or completed activity
    → projection position advances
```

Configuration changes do not implicitly replay earlier filtered observations.
Historical reinterpretation requires an explicit processor identity or
checkpoint reset policy.

## Delivery and replay

Delivery between stages is at least once.

- equivalent replay is idempotent
- conflicting output at the same durable identity is rejected
- state/activity projection state and derived outputs commit atomically
- canonical-store failure leaves raw processing progress unchanged
- projection-store failure leaves canonical input eligible for retry
- restart resumes after independently persisted stage progress

Canonical positions may contain gaps because unmapped raw observations are
still successfully processed. Readers therefore order by position and do not
require positions to be contiguous.

## Provider-neutral stage boundary

Canonical output is never passed directly from the mapping processor to the
state/activity processor. The mapping stage writes through
`IMappedMachineObservationSink`, and the next stage resumes through
`IDurableMappedObservationReader` and `MappedObservationProcessingRuntime`.
This keeps both processing stages independently checkpointed and retryable and
allows a future durable provider to replace the in-memory canonical store
without changing Edge orchestration.

## Multi-machine and multi-stream composition

The Edge composition root may register one or more observation streams. Every
stream receives its own:

- `MachineSignalMappingConfiguration`
- raw-to-canonical processor/runtime
- canonical-to-state/activity processor/runtime
- independent raw processing checkpoint
- independent state/activity projection

Canonical and projection stores may be shared because their durable keys include
stream identity. Processor identities remain stable across streams, while
progress stays isolated by processor plus stream.

For a single stream, `ObservationProcessing:Mappings` remains supported. When
multiple streams are registered, mappings are bound explicitly under
`ObservationProcessing:Streams`, with each entry identifying its `MachineId`,
`StreamKey`, and mapping collection. Missing, duplicate, or extra stream
configuration fails during composition rather than silently applying another
machine's mapping.

## Edge composition

The Edge composition root registers:

- the selected raw observation persistence provider
- machine/stream-specific canonical mapping configuration
- raw-to-canonical runtimes
- durable canonical reader/store
- canonical-to-state/activity runtimes
- state/activity projection store
- one hosted coordinator over all configured stream pipelines

A selected raw persistence provider must implement
`IDurableObservationReader` and
`IObservationProcessingCheckpointStore`. Missing capabilities cause a clear
startup failure. The current complete end-to-end composition uses the in-memory
reference provider.

## Conformance proof

The in-memory conformance scenario proves:

- processing across multiple durable batches
- acknowledgement of unmapped raw observations
- canonical observations that produce no state transition
- sparse canonical positions
- restart/resume without duplicate canonical or derived output
- canonical sink failure without raw checkpoint advancement
- projection commit failure followed by equivalent retry
- independent mappings and progress across machines and streams

## Deferred boundaries

FC-024 intentionally excludes:

- SQL Server canonical and state/activity persistence
- production and shift context enrichment
- metric-input derivation and aggregation
- reporting queries
- downtime reason classification
- implicit historical reprocessing

These belong to subsequent provider and factory-domain slices.
