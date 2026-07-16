# Contributing to LFTP Pilot

LFTP Pilot is a Windows 11 native application built around a separately
distributed LFTP/MSYS2 runtime. Contributions must preserve the process,
credential, path-validation, runtime-integrity, and update-signing boundaries
documented in `AGENTS.md` and `SECURITY.md`.

## Development setup

Use the .NET SDK version pinned by `global.json`. A project-local installation
may be placed in `.dotnet`, which is ignored by Git.

```powershell
.\.dotnet\dotnet.exe restore LFTPPilot.slnx --locked-mode -r win-x64
.\.dotnet\dotnet.exe build LFTPPilot.slnx -c Release --no-restore
.\.dotnet\dotnet.exe test tests\LFTPPilot.Tests\LFTPPilot.Tests.csproj -c Release --no-build
```

When changing a package reference, regenerate the affected committed
`packages.lock.json` files with `--force-evaluate`, review the resolved versions
and hashes, then return to `--locked-mode` for validation.

Runtime binaries, certificates containing private keys, credentials, and
generated packages must never be committed.

## Pull requests

- Create a focused branch from `main`.
- Add deterministic tests for Core, Engine, Agent, and release-tool behavior.
- Keep destructive mirror changes preview-gated.
- Document user-visible changes in `CHANGELOG.md`.
- Include the exact checks run in the pull-request description.
