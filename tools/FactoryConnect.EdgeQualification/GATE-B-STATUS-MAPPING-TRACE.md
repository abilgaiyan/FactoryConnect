# PR #180-B — MOXA status mapping verification

Source reviewed on the PR #180 branch:
- `src/FactoryConnect.Protocols.MTConnect/MtConnectObservationParser.cs`
- `src/FactoryConnect.Core/Machines/MachineStateEvaluator.cs`
- `src/FactoryConnect.Core/Machines/MachineSignalMappingProcessor.cs`
- `src/FactoryConnect.Abstractions/MachineSignalMappingDefinition.cs`

## Source-verified rules

MTConnect events parse to `SignalType.Enumeration`. Literal `UNAVAILABLE` parses to null and `ObservationQuality.Uncertain`; otherwise the trimmed event text is retained with `ObservationQuality.Good`.

`MachineStateEvaluator` treats the canonical `OperationalState` signal as authoritative, even when its quality is uncertain: in that case the result is Unknown and must not fall back to stale Boolean signals. For a single good Enumeration operational state, the **case-sensitive** accepted values are `RUNNING`, `IDLE`, `FAULT`, `STOPPED`; all other values are Unknown.

`MachineSignalMappingProcessor` maps retained observations according to the **commissioned** `MachineSignalMappingConfiguration`; only mapped observations contribute to the mapped evaluation input high-water. An address named `moxa001_status` is not automatically mapped to `OperationalState`.

## Observed local qualification baseline (operator-supplied SQL preflight)

MOXA acquisition checkpoint: instance `1793867780`, next sequence `27`. Retained raw frontier `14328`, activity authority position `14324`, revision `4703`, mapping checkpoint `14328`. Raw position `14324`: `moxa001_status`, NULL, uncertain; historical `RUNNING` and `IDLE` values exist on the preceding instance.

## Fixture readiness

The source evaluator accepts RUNNING/IDLE as canonical states, but this does **not** establish that the commissioned MOXA mapping binds `mtconnect:moxa001_status` to `OperationalState` at the test time, or that the candidate timestamp is chronologically eligible. Verify the exact commissioned JSON loaded by the Edge host, including machine identity, stream identity, mapping, type and configuration version. Do not publish the full production configuration or secrets in this PR.

Before emitting a fixture sample, verify that the test agent can legitimately serve same-instance sequence 27 and later observations, and that source timestamps do not precede the current committed activity start. The original source must never be impersonated against production; only an isolated fixture database and local controlled endpoint may be used.

No fixture writes or host tests have been executed. Gate B remains OPEN; production HOLD.
