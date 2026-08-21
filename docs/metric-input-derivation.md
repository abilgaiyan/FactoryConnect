# Metric Input Derivation

## Purpose

Metric input derivation converts recorded shop-floor facts into the normalized inputs consumed by the Metric Calculation Engine.

```text
Machine Activity + Production Schedule + Production Entries
                         ↓
                Metric Input Derivation
                         ↓
                Normalized Metric Inputs
                         ↓
                Metric Calculation Engine
```

## Derived Inputs

The first implementation derives:

- `apt` — sum of Running machine activity duration, expressed in hours.
- `pot` — planned operating time from the production schedule, expressed in hours.
- `produced-quantity` — sum of production entry quantities within the requested scope.
- `good-quantity` — sum of produced quantity minus in-process rejected quantity.

## Scope

Derivation is performed for one Company + Site + Machine + Shift + Production Date. Production schedule and production entries must belong to the same scope.

## Production Reference Time

`pnot` is deliberately not derived in FC-009. Its business definition can vary by company/site and requires a production-standard/reference-time source. It remains an extensible metric input rather than an assumed formula.

## Architectural Boundary

Production facts remain unchanged. Derivation creates calculation inputs from those facts; it does not reinterpret or mutate the source records.

Company/site metric policy continues to decide how the resulting inputs are used by the Metric Calculation Engine.
