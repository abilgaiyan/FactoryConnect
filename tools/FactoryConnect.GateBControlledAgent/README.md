# Gate B controlled MTConnect Agent

This executable is a **loopback-only source fixture** for PR #180. It does not access SQL Server, load factory configuration, start the Edge host or mutate observations.

Run from the PR branch:

```powershell
dotnet run --project tools/FactoryConnect.GateBControlledAgent -- --serve --phase first
```

The endpoint is `http://127.0.0.1:51027/sample?from=27`. It deliberately returns fixture-owned instance `9000001`, with `firstSequence=1`, to force the existing MTConnect runtime to recognize the old-to-new instance boundary. A subsequent `/sample?from=1` returns sequence 1, `RUNNING`. In the **first** phase, sequence 2 is deliberately unavailable, preventing the second state transition before restart. Stop the agent and, only after the processing host has stopped and a fresh host has been started with the committed checkpoint, start `dotnet run --project tools/FactoryConnect.GateBControlledAgent -- --serve --phase second`. In the **second** phase only `/sample?from=2` returns sequence 2, `IDLE`. Timestamps are fixed and later than the recorded MOXA activity start. Unsupported paths, phases and cursors return HTTP 400. Never use phase switching to bypass a failed source admission.

**Important limitations:** this fixture is a deterministic protocol source, not a realistic continuously advancing agent. Do not configure any production Edge service or production database to poll it. Do not start the existing Edge acquisition host with commissioned factory endpoints while qualifying it. This tool has no durable writer and does not establish source-generation admission, mapping qualification, SQL acceptance, activity advancement, restart recovery or production release readiness.

Before wiring a separate, bounded acquisition runner to the disposable clone, independently verify the commissioned configuration and exact database target, recheck the previously verified publication head, ensure no other writer is running, and reject any unexpected baseline. Only the production runtime and `MtConnectDurableObservationSink` may commit fixture observations; never edit SQL checkpoints or raw evidence directly. If admission fails, stop and report BLOCKED.

This executable is not registered by the production Edge host.
