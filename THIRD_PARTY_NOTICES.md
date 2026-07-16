# Third-party notices

LFTP Pilot is designed to distribute a locked Windows runtime assembled from
MSYS2 packages including LFTP and OpenSSH. Runtime binaries are not committed
to this repository.

The exact native package/file inventory and selected self-contained .NET
runtime pack are locked under `build/runtime-lock`. Production NuGet and
Windows App SDK/WinUI dependencies are locked by each source project's
`packages.lock.json` and derived from restored `project.assets.json`; test-only
dependencies are excluded from release evidence.

Before any binary release, the repository must contain independently reviewed
license, redistribution-package, and source-obligation evidence for every
native and managed production input. `build/Test-LicenseEvidence.ps1`
deliberately blocks publication until schema 3 evidence is exact and complete;
the checked-in template is not complete. The release tooling then generates
cryptographic checksums, a production-scoped SBOM, and an allowlisted evidence
archive from only those reviewed inputs.
