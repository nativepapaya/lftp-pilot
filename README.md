# LFTP Pilot

LFTP Pilot is a native Windows 11 dual-pane client built around the real
[LFTP](https://lftp.yar.ru/) transfer engine. It is a new product with
independent source history, native WinUI controls, session-oriented connection tabs,
first-class transfer and mirror planning, and an optional background agent.

The project is under active development. Planned trusted-test packages use an
explicitly reviewed self-signed development certificate; the current source
tree does not yet authorize a public binary release.

The current bootstrap is an engineering preview, not a 1.0 release. See
[Implementation status](docs/IMPLEMENTATION_STATUS.md) for the exact working
surface and the remaining managed-edit, folder-control, and signed-update
acceptance work.

## Product direction

- Native WinUI 3 dual-pane workspace with independent connection tabs
- Non-modal recursive remote name search isolated from persistent browsing
- SFTP, FTP, FTPES, implicit FTPS, and plain FTP
- LFTP queues, parallel transfers, segmented downloads, mirror/reverse mirror,
  bandwidth controls, and remote-to-remote jobs
- Reusable mirror definitions plus fresh dry-run previews with mandatory review
  before destination deletions
- Managed remote editing with a package-scoped cache, strong local/remote
  identities, a fixed trusted Notepad launcher, and reviewed staged promotion
- Isolated advanced LFTP console with local shell execution disabled by default
- Optional tray-hosted background transfers and run-once schedules
- Self-contained x64 MSIX with Windows App Installer updates

## Build

```powershell
.\.dotnet\dotnet.exe restore LFTPPilot.slnx --locked-mode -r win-x64
.\.dotnet\dotnet.exe build LFTPPilot.slnx -c Release --no-restore
.\.dotnet\dotnet.exe test tests\LFTPPilot.Tests\LFTPPilot.Tests.csproj -c Release --no-build --no-restore
```

`run-checks.ps1` runs the same locked restore, solution build, xUnit suite, and
release-tool tamper tests used by CI.

The opt-in controlled protocol matrix uses disposable loopback servers and the
real packaged LFTP runtime without installing Docker, WSL, or a Windows
service:

```powershell
.\build\Test-ProtocolMatrix.ps1
```

See [the protocol-lab contract](eng/protocol-lab/README.md) for its isolated
test-only dependencies and trust behavior.

The local `.dotnet` SDK is ignored. Install the SDK version pinned by
`global.json` when it is not already available.

## License

LFTP Pilot application code is MIT licensed. LFTP, OpenSSH, MSYS2 runtime
components, CA data, the self-contained .NET runtime, WinUI/Windows App SDK,
WebView2, and other managed production dependencies have their own license and
redistribution requirements. See `THIRD_PARTY_NOTICES.md`,
`docs/LFTP-RUNTIME.md`, and `third-party-licenses/README.md`. The committed
managed-evidence template is intentionally incomplete, so this source tree does
not yet authorize a public binary release.
