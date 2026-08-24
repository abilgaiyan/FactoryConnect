# FC-022 — Pluggable Persistence Provider Architecture

## Goal

FC-022 separates the durable observation-ingestion contract from the concrete persistence technology used to implement it.

The central invariant is:

```text
available providers != active provider
```

A provider package may make a persistence implementation available without registering `IObservationIngestionStore` directly. The neutral persistence layer selects exactly one configured provider and owns the single active store registration.

## Architecture

```text
composition root
    |
    +-- AddFactoryConnectPersistence(configuration)
    |
    +-- register available provider factories
            |
            +-- InMemory
            +-- future SqlServer
            +-- future PostgreSql
            +-- future MongoDb

                    |
                    v
          validate unique provider keys
                    |
                    v
          select Persistence:Provider
                    |
                    v
          activate exactly one factory
                    |
                    v
        IObservationIngestionStore
```

`FactoryConnect.Persistence` is provider-neutral. It does not contain a switch statement and does not know concrete provider names.

## Selection Contracts

`PersistenceOptions` owns the configured provider key after normalization.

`PersistenceProviderKey` normalizes keys by trimming whitespace and applying invariant case normalization. Provider matching and duplicate detection therefore use the same canonical representation.

`IPersistenceProviderRegistration` describes how an available provider can create an ingestion store:

```csharp
public interface IPersistenceProviderRegistration
{
    string ProviderKey { get; }

    IObservationIngestionStore Create(IServiceProvider services);
}
```

Registration does not activate the provider.

`PersistenceProviderRegistration` is the default factory-backed implementation of that contract.

## Composition Lifecycle

The supported lifecycle is:

```text
register neutral persistence selection
        |
register provider factories
        |
resolve IObservationIngestionStore
        |
validate unique normalized keys
        |
select configured key
        |
activate exactly one provider
```

Example:

```csharp
builder.Services.AddFactoryConnectPersistence(builder.Configuration);
builder.Services.AddInMemoryPersistenceProvider();
```

with configuration:

```json
{
  "Persistence": {
    "Provider": "InMemory"
  }
}
```

The in-memory provider key belongs to `FactoryConnect.Infrastructure`, not to `FactoryConnect.Persistence`.

## Failure Semantics

FC-022 fails explicitly when:

- `Persistence:Provider` is missing or whitespace;
- the configured provider key has no registration;
- two available providers normalize to the same key;
- another component has already registered `IObservationIngestionStore` directly;
- an activated provider factory returns no store.

These failures prevent ambiguous persistence ownership.

## Exactly-One Activation

Only the selected registration's factory is invoked.

Registering multiple available providers does not instantiate them, and unselected providers remain inactive. The active `IObservationIngestionStore` is registered as a singleton, so repeated resolution returns the same activated store.

## Current Provider

FC-022 keeps the existing `InMemoryObservationIngestionStore` as the initial provider implementation.

`AddInMemoryPersistenceProvider()` registers only its provider factory. It does not directly register `IObservationIngestionStore`.

This preserves existing FC-020/FC-021 ingestion and checkpoint behavior while moving persistence choice to composition time.

## Extension Model

Future providers follow the same pattern without modifying the neutral persistence assembly:

```text
FactoryConnect.Persistence.SqlServer
    |
    +-- AddSqlServerPersistenceProvider(...)
            |
            +-- provider key
            +-- provider configuration
            +-- factory
            +-- concrete IObservationIngestionStore
```

A future provider package can therefore be added through normal composition-root registration rather than a central provider switch.

## Scope

Included in FC-022:

- neutral `FactoryConnect.Persistence` project;
- persistence provider options and key normalization;
- provider registration/factory contract;
- duplicate-key validation;
- configured-provider selection;
- exactly-one store activation;
- rejection of direct competing store registration;
- in-memory provider registration through the new model;
- Edge composition through `Persistence:Provider`;
- tests proving provider-selection invariants.

Not included:

- SQL Server implementation;
- MongoDB implementation;
- PostgreSQL implementation;
- migrations or schema management;
- provider-specific connection-string validation;
- runtime provider switching;
- multiple active ingestion stores.

Those belong to later provider-specific slices such as FC-023.
