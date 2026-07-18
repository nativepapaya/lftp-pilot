# Third-party notices

LFTP Pilot is designed to distribute a locked Windows runtime assembled from
MSYS2 packages including LFTP and OpenSSH. Runtime binaries are not committed
to this repository.

The exact native package/file inventory and selected self-contained .NET
runtime pack are locked under `build/runtime-lock`. Production NuGet and
Windows App SDK/WinUI dependencies are locked by each source project's
`packages.lock.json` and derived from restored `project.assets.json`; test-only
dependencies are excluded from release evidence.

The reviewed `third-party-licenses/licenses-manifest.json` binds license,
redistribution-package, and source-obligation evidence for every native and
managed production input. `build/Test-LicenseEvidence.ps1` blocks publication
unless that schema 3 evidence exactly matches the current production graph.
The release tooling generates cryptographic checksums, a production-scoped
SBOM, and an allowlisted evidence archive from only those reviewed inputs.

The separately retained incomplete template remains a fail-closed starting
point for a future runtime or dependency revision. Any graph change requires a
fresh manifest generation and review; it cannot inherit this release decision.
