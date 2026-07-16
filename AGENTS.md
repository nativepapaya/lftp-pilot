# LFTP Pilot repository guidance

This repository contains the native Windows successor product, LFTP Pilot. It
has independent Git history and must never write to or migrate data from LFTP
Commander.

## Platform and toolchain

- Target Windows 11 x64 with .NET 10 and WinUI 3.
- Use the project-local SDK at `.dotnet\dotnet.exe` when a matching system SDK
  is unavailable. Never commit `.dotnet`.
- Keep application code native. Do not add Electron, WebView2, a Node runtime,
  WPF, or third-party data-grid controls.
- The shipped LFTP/MSYS2 runtime remains x64 and must be executed only after
  its locked inventory and hashes have been authenticated.

## Trust boundaries

- `LFTPPilot.App` is UI only; it communicates with the background Agent over a
  bounded, versioned, current-user-only protocol.
- `LFTPPilot.Agent` owns LFTP processes, durable jobs, and background lifetime.
- Validate file paths, profile identities, activation payloads, console input,
  and destructive mirror approvals at the owning process boundary.
- Local shell execution from the advanced console stays disabled by default.
- Every LFTP process tree must belong to a kill-on-close Windows Job Object.
- Never execute a mirror dry-run script. Parse it for display and regenerate a
  validated command for the approved job.

## Change discipline

- Keep `main` releasable and work through short-lived `agent/*` branches after
  the bootstrap commit.
- Add regression tests for bugs in Core or Engine behavior.
- Preserve runtime integrity, signing, license/source, SBOM, update, and
  packaged-smoke gates.
- Do not commit runtime binaries, build output, credentials, signing keys,
  certificates containing private keys, or application data.

## Validation

Run the narrowest relevant tests while developing. Before handoff run:

```powershell
.\.dotnet\dotnet.exe restore LFTPPilot.slnx --locked-mode -r win-x64
.\.dotnet\dotnet.exe build LFTPPilot.slnx -c Release --no-restore
.\.dotnet\dotnet.exe test tests\LFTPPilot.Tests\LFTPPilot.Tests.csproj -c Release --no-build
```

Every project has a committed `packages.lock.json`. Dependency changes must
update and review the affected lock files; CI and release revalidation never
fall back to an unlocked restore.

Packaging and release changes must additionally build and inspect the real
unsigned MSIX and generated App Installer feed.
