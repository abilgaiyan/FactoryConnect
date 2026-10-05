# RBS-13 — Factory boot orchestration

Repository implementation candidate; Windows executable acceptance and factory
commissioning remain open. Entry baseline: `b72b190a71d11c327554625e0b1fe9e480d5fefa`.

One boot task remains the authority. Acquisition retains ownership of FANUC,
SHDR readiness, MTConnect and `Verify-Acquisition.ps1`. The repository does not
rewrite or package those existing factory scripts. It relies on their established
contract: `Start-Acquisition.bat` exits zero only after acquisition verification.

`Start-FactoryConnectSystem.ps1` invokes that batch from the install root, waits
for its exit, and invokes no FactoryConnect runtime on nonzero exit. Only after
success does it validate `current` as one direct, 40-character release junction
inside `releases`, require `current/Start-FactoryConnectRuntime.ps1`, and invoke
that script with the install root. Runtime startup retains its existing ownership,
health, intent and deployment-lock authority. The orchestrator neither terminates
children nor interprets runtime ownership. It has its own exclusion lock across
the complete acquisition/runtime chain; it never holds the deployment lock while
calling RBS. Nonzero child exits propagate to the caller; selection/invocation
errors return nonzero. Unique `deployment/logs/system-<attempt>` folders preserve
acquisition output, runtime output and `system-start.json` phase/exit evidence.
These logs do not establish machine lifecycle or quantity authority.

The release builder requires and packages the orchestrator and its installer,
declares both in `release.json`, and includes both in the existing full manifest.
`Install-FactoryConnectSystemStartup.ps1` is an explicit commissioning operation:
under `deployment.lock`, it rejects unresolved runtime intent, resolves the current
release, validates metadata and the orchestrator's manifest hash, then atomically
copies those exact bytes to `<InstallRoot>/Start-FactoryConnectSystem.ps1`.
Identical bytes are an installation no-op. It does not invoke acquisition/runtime,
change selection, commission application configuration, migrate, or create tasks.
Normal deployment remains unchanged. On later orchestration upgrades, run the
reviewed installer again; moving `current` alone does not replace the root script.
Application releases can change without changing the task's action.

After review and factory authorization, the installation operation is:

```powershell
& D:\FactoryConnect\current\Install-FactoryConnectSystemStartup.ps1 -InstallRoot D:\FactoryConnect
```

The proposed stable task action is Windows PowerShell with arguments:

```text
-NoProfile -ExecutionPolicy Bypass -File D:\FactoryConnect\Start-FactoryConnectSystem.ps1
```

Keep the existing SYSTEM/Highest, boot delay and IgnoreNew policy. Task timeout
must be reviewed for the entire chain during commissioning; no task setting is
changed by this implementation. The installer is not called at boot. Do not add a
second delayed task as a substitute for acquisition success. First commissioning
should invoke the stable script on the running factory, verify acquisition success
and genuine RBS `AlreadyRunning`, then review a separately authorized reboot.
No factory action or reboot is included in this repository change.

Focused proof:

```powershell
.\tests\deployment\Invoke-FactoryBootOrchestrationConformance.ps1
```

The disposable Windows proof uses an acquisition batch and packaged RBS stub;
it proves orchestration ordering, exit propagation, missing/invalid selection,
missing runtime authority, runtime/config/acquisition preservation, exact stable
installation and replacement, manifest failure, unresolved-intent exclusion and
installer lock contention. The stub's `AlreadyRunning` output is delegation
proof only, not real-machine commissioning or a rerun of B01–B12. `-ContractOnly`
checks parse/package/scope contracts without claiming executable acceptance.
Factory acquisition, task commissioning and a natural reboot remain separate gates.
A2 and authoritative ProducedQuantity remain unchanged and blocked.
