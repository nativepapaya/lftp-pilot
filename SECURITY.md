# Security policy

## Supported versions

LFTP Pilot is pre-release software. Security fixes are applied to the latest
development line only.

## Reporting a vulnerability

Please use GitHub private vulnerability reporting for this repository. Do not
open a public issue for credential disclosure, command injection, path escape,
update-signing, or bundled-runtime integrity findings.

## Security model

- Credentials are protected for the current Windows user and never passed in
  process command lines.
- UI and transfer processing run in separate processes with bounded messages.
- Destructive mirror jobs require a fresh preview and explicit approval.
- Advanced-console local shell execution is disabled by default.
- Bundled runtime files are authenticated before execution.
- Updates must preserve the MSIX identity, publisher, and signing key.
- Binary publication requires GitHub build provenance for the exact unsigned
  MSIX, byte-identical payloads before and after signing except for the package
  signature entry, an immutable remote release tag bound to the attested source
  commit, and exact certificate continuity with the previous immutable release.
- Production SBOM and legal-evidence gates exclude test dependencies, include
  selected self-contained runtime packs, and fail closed until every native and
  managed redistributed dependency has independently reviewed evidence.
