# FactoryConnect

Industrial machine connectivity and factory data platform built with .NET.

## Vision

FactoryConnect provides the reliable factory-data foundation between industrial machines and higher-level business and AI applications.

```text
Machine
  ↓
I/O Gateway / Industrial Protocol
  ↓
FactoryConnect Edge
  ↓
Canonical Machine State
  ↓
Production Events
  ↓
Factory Data
  ↓
Applications / Analytics / AI
```

## Technology

- C#
- .NET 10
- ASP.NET Core
- Worker Services
- SQL Server / Entity Framework Core
- Modbus TCP
- MTConnect
- React + TypeScript (planned)

## Architecture Principles

1. FactoryConnect owns the factory and machine domain.
2. Protocols are adapters and must not leak into the domain model.
3. Machine signals are translated into canonical machine states.
4. State transitions produce domain-level production events.
5. The Edge runtime must operate independently of the dashboard UI.
6. Hardware is replaceable through connector abstractions.
7. The software must be testable without physical factory hardware.
8. PulseStackAI is a separate platform and may consume FactoryConnect data for AI orchestration.

## Initial Scope

The first pilot targets factory machine connectivity, beginning with digital machine signals through industrial Ethernet I/O gateways and Modbus TCP.

## Project Status

**FC-001 — Foundation:** In progress

## License

To be defined.
