# FactoryConnect

Industrial machine connectivity and factory data platform built with .NET.

## Vision

FactoryConnect provides a reliable factory-data foundation between industrial machines and higher-level business, analytics, and AI applications.

```text
Machine
  ↓
I/O Gateway / Industrial Protocol
  ↓
FactoryConnect Edge
  ↓
Canonical Observations
  ↓
Durable Ingestion + Continuity Checkpoint
  ↓
Factory Data
  ↓
Applications / Analytics / AI
```

## Current Capabilities

- Canonical machine and observation contracts
- Modbus TCP and MTConnect protocol adapters
- MTConnect discovery, current and sequence-aware sample acquisition
- Continuous Edge acquisition with transient retry and continuity recovery
- Durable observation ingestion with atomic cursor checkpointing
- Pluggable persistence-provider selection
- In-memory persistence provider
- SQL Server persistence provider with transactional commits, idempotent replay, exact stream identity, and same-stream concurrency protection
- Shared persistence conformance tests across providers

## Technology

- C#
- .NET 10
- ASP.NET Core
- Worker Services
- Microsoft.Data.SqlClient
- SQL Server
- Modbus TCP
- MTConnect
- xUnit
- React + TypeScript (planned)

## Architecture Principles

1. FactoryConnect owns the factory and machine domain.
2. Protocols are adapters and must not leak into the domain model.
3. Machine signals are translated into canonical observations and machine state.
4. Acquisition continuity and durable persistence are explicit architectural boundaries.
5. Persistence providers are replaceable; available providers are not automatically active providers.
6. The Edge runtime must operate independently of the dashboard UI.
7. Hardware is replaceable through connector abstractions.
8. The software must be testable without physical factory hardware.
9. PulseStackAI is a separate platform and may consume FactoryConnect data for AI orchestration.

## Persistence

FactoryConnect selects exactly one observation-ingestion provider at the composition root.

```json
{
  "Persistence": {
    "Provider": "InMemory"
  }
}
```

For SQL Server, provider-specific configuration is supplied separately:

```json
{
  "Persistence": {
    "Provider": "SqlServer"
  },
  "PersistenceProviders": {
    "SqlServer": {
      "ConnectionString": "<connection-string>"
    }
  }
}
```

Provider registration remains separate from provider activation. SQL Server configuration is validated only when SQL Server is selected.

## Initial Deployment Scope

The first deployment scope targets industrial machine connectivity through Ethernet-capable controllers and retrofit I/O gateways, with MTConnect and Modbus TCP feeding the same canonical FactoryConnect model.

## Project Status

FactoryConnect has progressed through **FC-023 — SQL Server Persistence Provider**. The current foundation includes continuous MTConnect acquisition, continuity recovery, durable observation ingestion, pluggable persistence, and SQL Server durability with shared provider conformance coverage.

## License

To be defined.
