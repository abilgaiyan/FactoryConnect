# FactoryConnect Architecture

## Boundary

FactoryConnect is the factory and machine connectivity platform. It is intentionally separate from PulseStackAI, which remains responsible for AI agents, workflows, and AI orchestration.

## Runtime flow

```text
Machine
  ↓
I/O Gateway / Industrial Protocol
  ↓
FactoryConnect Edge
  ↓
Signal Mapping
  ↓
Canonical Machine State
  ↓
Production Events
  ↓
Persistence / API
  ↓
Dashboard / Analytics
  ↓
PulseStackAI (future integration)
```

## Layering

```text
FactoryConnect.Abstractions
        ↑
FactoryConnect.Core
        ↑
FactoryConnect.Infrastructure
        ↑
FactoryConnect.Edge

FactoryConnect.Protocols.Modbus ──→ Abstractions
FactoryConnect.Api              ──→ Core + Infrastructure + Abstractions
```

Protocol implementations remain adapters. The factory domain must not depend on Modbus register addresses, coils, or vendor-specific concepts.

## Initial connectivity

The first pilot uses industrial Ethernet I/O gateways and Modbus TCP. MTConnect and other protocols can be added later without changing the canonical machine domain.

## Development principle

Physical hardware must not be required for core development. A simulator/fake connector will exercise the same contracts used by real industrial connectors.
