# Mayhem Quasar — remote standalone server roadmap

**Status:** Controller-only safety foundation implemented on `feature/remote-standalone-hosts`.
**Not yet implemented:** Deploying or starting standalone game servers on a remote Host executor.
This branch must not be installed on production TX/NY-01 until integration tests and release preparation are complete.

## Deployment invariant

- **TX** runs the Quasar control plane and stores configuration, permissions and orchestration state. It must **never launch a standalone Space Engineers game process**.
- **NY-01**, then **NY-02** and **FL-01**, run Windows Host executors, SteamCMD, Dedicated Server, Magnetar, saves and game processes.
- Remote Host communication must use the existing authenticated outbound Host tunnels. Host command listeners remain bound to loopback; do not publish control ports through the home router.
- New game servers need independent per-host directories, credentials, Steam/game ports and process ownership. AMP/Torch/Nexus installations are out of scope and must not be scanned or imported automatically.

## Safety foundation (this branch)

- `Quasar:ControllerOnly=true` in the **fork's packaged default configuration**, or `QUASAR_CONTROLLER_ONLY=true` as a runtime environment override, blocks the local standalone process supervisor.
- The fleet and server-list New Server buttons are disabled while controller-only mode is active, until remote placement actually exists.
- Direct starts, administrative restarts, the process launch implementation and periodic auto-start reconciliation are guarded independently.
- Existing standalone server definitions and world directories are not automatically deleted. Stop any test game process on TX using the existing version **before** switching, because the new mode does not kill pre-existing processes.
- The fork's default web updater is disabled, preventing accidental replacement with the upstream `CometWorks/quasar` build. **An existing deployed appsettings.json is preserved** and must be configured explicitly.
- Cluster tooling may still stage or prepare artifacts on TX; the safety guard is specifically about **game process execution**. A later task will move all runtime provisioning to the selected host.

## Remote standalone server implementation plan

1. **Persistent placement identity.** Add a required, immutable `HostMachineId` to every *new* standalone server. Existing upstream definitions remain local for migration compatibility. No fallback to TX when a selected host is offline.
2. **Host-owned state.** Implement versioned standalone commands on the Windows Host for provisioning, create/update, lifecycle, status, logs, console and deletion. Scope all operations to authenticated Host ID + server ID. Reuse the authenticated tunnel, enforce path containment and validate port availability on the **selected** machine, not the controller.
3. **Runtime provisioning.** The Host downloads verified SteamCMD, Space Engineers Dedicated Server and Windows Magnetar assets, with hashes, independent roots and idempotent retries. TX sends definitions and world/template/profile inputs; it does not install game binaries.
4. **Life cycle and monitoring.** The Host owns and reconciles processes across TX disconnects and reboots. TX displays live remote state and routes start/stop/restart, console and logs to the owner Host. Guard against duplicate starts, dangling goals and unexpected host reassignment.
5. **UI.** Replace the disabled New Server control with a host picker listing **connected Windows Hosts**. Restrict choices to hosts that report required standalone capabilities. Keep per-host port collision checking and prevent implicit local deployment.
6. **Updates.** Keep upstream Quasar as a Git remote, forward-port reviewed upstream releases into this fork and ship a matched controller/Host bundle. Never auto-stage or auto-apply a CometWorks web/Host binary to a Mayhem customized installation.
7. **Tests.** Prove: TX starts no game process, remote NY-01 create/start/stop, no offline-host fallback, isolated files/ports, upgrades/reboots, disconnect/reconnect, game-process adoption, and no changes to existing AMP/Torch/Nexus installations.

## Current boundary

This is an **incremental branch**, not a completed fork or remote server manager.
Do not deploy a server through the standalone **New Server** path until steps 1–5 and integration tests pass. Cluster enrollment remains separate from standalone placement.

## Local development

The .NET 10 SDK and referenced Magnetar/Space Engineers build dependencies are required for the complete test suite.
Run `dotnet test Quasar.Tests/Quasar.Tests.csproj --filter FullyQualifiedName~ControllerOnlyModeTests` after restoring the repository's documented dependencies.
