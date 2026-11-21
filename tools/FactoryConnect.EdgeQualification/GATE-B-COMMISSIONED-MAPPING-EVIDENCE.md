# Gate B — commissioned MOXA mapping evidence

Source: operator-supplied read-only excerpt from local `factory-backups/config/edge.production.json`, lines 376–390, November 9, 2026. This is not a checked-in production configuration.

- MachineId: `bfa0dc97-9a68-45ed-b189-1dacb7d863f0`
- StreamKey: `mtconnect:DEWAS-SR-MOXA-001`
- Mapping: Source `mtconnect`, Address `moxa001_status`, SignalKey `state.operational`, Type `Enumeration`, Invert `false`
- `MoxaStatusNormalization`: Source `mtconnect`, Address `moxa001_status`

**Disposition: commissioned address-to-canonical-signal mapping verified.**

Previously observed qualification SQL baseline: acquisition instance `1793867780`, NextSequence `27`, raw frontier `14328`, activity position `14324`, revision `4703`, ActiveStartedAt `2026-11-05T08:36:20.736297Z`.

This does not independently verify the active normalization implementation, permitted fixture timestamps, acquisition publication ancestry or safe mutation target. Fixture admission remains gated on a new isolated disposable database and controlled source; never write the preserved incident or qualification databases.

No fixture execution or SQL mutation is claimed. Gate B remains open; production deployment HOLD.
