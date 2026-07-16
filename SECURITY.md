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
  process command lines. Their binding includes the exact transport endpoint,
  user name, authentication purpose, and profile identifier; changing any
  security-significant connection identity revokes the old binding.
- SFTP credentials are not resolved or sent until host trust exists. First-use
  and changed keys require explicit fingerprint review; probes disable
  authentication, and real sessions use a profile/endpoint-bound one-key
  `known_hosts` file with strict checking. Changed trust cannot be replaced
  while related sessions, jobs, schedules, or edits are active or their LFTP
  process cleanup is incomplete.
- UI and transfer processing run in separate processes with bounded messages.
  An interrupted, malformed, or mismatched control reply invalidates that pipe
  before another request can use it. Agent shutdown stops admission and drains
  in-flight client work before disposing shared workspace state.
- Destructive mirror jobs require a fresh preview and explicit approval.
- Transfer plans and mirror previews are consumed as durable job identifiers.
  Unknown replies are reconciled only with the exact original identifier, and
  terminal validation or admission failures remain recorded so cache eviction
  cannot authorize a late replay. An unresolved deletion-capable mirror blocks
  any fresh preview or approval in the App. Mirror approval also binds a digest
  of the exact preview metadata and ordered actions shown to the user to the
  Agent-held, HMAC-authenticated dry run.
- Reviewed remote-to-remote plans are single-use capabilities. The Agent uses
  the plan identifier as the job identifier and makes duplicate or lost-reply
  submissions converge before another LFTP process can start. Both source and
  destination connection identities are pinned at issuance and must still match
  the active sessions before the Agent performs remote validation.
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
