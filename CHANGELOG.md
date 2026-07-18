# Changelog

All notable changes to LFTP Pilot will be documented in this file.

The project uses four-part MSIX package versions for monotonic App Installer
updates and human-facing `v1.0.<sequence>` release tags during trusted testing.

## Unreleased

### Fixed

- Third-party license evidence now uses repository-pinned LF bytes on every
  platform, and the release-tool suite checks every committed license hash
  after checkout. Windows line-ending conversion could previously invalidate
  an otherwise reviewed manifest only when the publication gate staged it.
- Release publication dry runs now create and validate the complete local
  staging set while suppressing only the final GitHub upload. PowerShell's
  inherited `-WhatIf` preference previously skipped nested provenance writes
  and made the documented review step fail before assets could be inspected.

### Added

- Complete schema 3 redistribution evidence now covers all 43 locked native
  runtime packages and all 17 managed production dependencies, with reviewed
  licenses, exact corresponding-source or public distribution archives,
  deterministic allowlisted staging, and explicit source-delivery decisions.
- NuGet evidence now records the locked normalized content hash separately
  from the raw SHA-512 of the signed public `.nupkg`; a regression fixture
  prevents release validation from conflating those distinct digests.
- Folder transfers now expose reusable package-scoped presets, ordered include
  and exclude globs, per-tree parallelism, and segmented-download controls in
  the native transfer dialog. The Agent validates and persists only bounded
  planning inputs, uses the same structured options for a fresh safety dry run
  and execution, and the controlled real-runtime matrix proves filtered
  parallel upload/download across FTP, opportunistic TLS, FTPES, implicit FTPS,
  and SFTP. Parallel directory execution also scopes LFTP temporary destination
  names off to avoid a reproduced LFTP 4.9.3 protected-data stall, restoring the
  process setting immediately afterward.
- Managed remote editing now has a real-runtime acceptance matrix across FTP,
  opportunistic TLS, FTPES, implicit FTPS, and SFTP. It proves reviewed cache
  uploads, concurrent delete/recreate conflicts, refresh, explicit overwrite,
  cleanup, and forced FTP/SFTP promotion rollback with a retryable edit. The
  App now also refuses to launch Notepad unless the Agent-returned file is a
  present regular non-reparse entry inside the package-owned edit cache.
- The controlled real-runtime matrix now proves interrupted segmented resume,
  recoverable failed-transfer retry, active rate-limited cancellation and
  isolated-process cleanup across every supported protocol. Dedicated FTP
  peers also verify direct FXP and LFTP's client-side fallback when both FXP
  arrangements are rejected, while SFTP/mixed and TLS-protected FTP routes use
  the explicit two-process managed relay.
- Remote files can now be dragged from a native pane to Explorer. Windows'
  delayed `StorageItems` request starts a bounded Agent-owned export only when
  Explorer accepts the drop; LFTP prepares freshly validated segmented
  downloads in an isolated package cache, and drag completion cancels or
  removes every managed copy without publishing remote paths as local files.
- Guarded directory transfers and approved mirrors now publish monotonic phase
  progress and advance through only those verbose LFTP actions that match the
  freshly reviewed dry run. Remote-to-remote jobs expose routing, validation,
  relay download/upload, cleanup, and FXP phases; managed relay downloads also
  report observed bytes without weakening the isolated temporary-file boundary.
- Native Windows shell integration is now connected end to end: the Agent owns
  terminal job notifications even after the App closes, notification clicks
  can only open the allowlisted Transfers route, active work drives taskbar
  state/progress, and Jump Lists expose Transfers, Settings, and bounded saved
  profile activations. Settings can also create a bounded sanitized support ZIP.
- Support-bundle generation now redacts quoted JSON secrets as well as text
  assignments and uses the ZIP format's deterministic 1980 timestamp floor;
  the previously unused builder could not write an archive with Unix epoch
  timestamps.
- SFTP and mixed-protocol remote-to-remote transfers now use an Agent-owned
  client relay with distinct source and destination LFTP processes, preserving
  each endpoint's credential redaction and pinned host-key configuration. The
  managed payload is freshly size-checked, never exposed through App contracts,
  and removed on success, failure, or cancellation; destination no-clobber is
  revalidated immediately before upload.
- The protocol lab now pins a mutually compatible `cryptography 48.0.1` and
  `pyOpenSSL 26.2.0` pair, retaining the security fix without making the
  hash-locked test environment impossible to install.
- SFTP profiles now support unencrypted and passphrase-encrypted OpenSSH
  private keys. Passphrases use LFTP's redacted in-memory credential channel,
  never process arguments or environment variables, and can optionally use
  profile/endpoint/user/key-bound DPAPI storage. The connection UI labels and
  remembers key passphrases separately from passwords, and the controlled
  runtime matrix covers both key forms plus first-use, unchanged, and rotating
  host-key trust.
- A disposable, repository-contained controlled protocol lab now runs the real
  Agent, Engine, and packaged LFTP runtime against loopback FTP, opportunistic
  FTP TLS, FTPES, implicit FTPS, and SFTP servers. It verifies password
  authentication, strict trust, Unicode browse/mutation, uploads, segmented
  downloads, moves, deletion, and cleanup without installing system services
  or changing the user's certificate store. Test-only Python dependencies are
  exact-version and distribution-hash pinned.
- The background Agent now owns a native Windows notification-area surface.
  Double-clicking it or choosing Open restores the transfer view through the
  allowlisted activation route, while the explicitly labeled stop command
  performs the same graceful Agent shutdown and process-tree cleanup as the
  App. After the last App disconnects, scheduled work and managed edits keep
  the Agent alive; once background work finishes, a two-minute recovery window
  expires before it exits automatically. An orphaned Agent that never receives
  an App connection follows the same policy after workspace restoration.
- Initial native Windows repository, architecture, and product foundation.
- Virtualized WinUI dual panes, persistent Agent sessions, native file
  operations, Quick Access bookmarks, transfer activity, and reviewed remote
  transfer routing.
- Profile-bound DPAPI secrets, authenticated named pipes, kill-on-close process
  trees, isolated read-only console sessions, and exact packaged-runtime trust.
- Explicit SFTP host-key enrollment and changed-key review before credentials
  can leave the App. The Agent probes with authentication disabled, persists
  only profile/endpoint-bound trust, and gives every real LFTP session an
  isolated one-key `known_hosts` file with strict checking and automatic host
  confirmation disabled. Replacements are blocked while dependent work is
  active or its LFTP process is still shutting down. Review endpoints and
  fingerprints are selectable for out-of-band comparison.
- Interrupted or malformed control-pipe exchanges now discard the connection
  before another request, Agent shutdown drains admitted client work before
  disposing workspace state, and job admission is serialized with session and
  profile removal through underlying process cleanup.
- Session tabs now survive an Agent restart as credential-free disconnected
  intent with stable IDs, ordering, and last local/remote paths. Reconnect is
  explicit and preserves the tab identity; ask-on-connect secrets are never
  persisted or replayed. Missing or changed profile identities are pruned
  before trust, credential, or network work, and durable close tombstones
  prevent failed process cleanup from resurrecting a closed tab.
- The shared Agent state writer now rejects malformed or future-dated job
  snapshots, orders asynchronous job captures before they can race, and
  preserves session tabs while job state changes. Persistence failures are
  reported without leaking state paths, and shutdown still cleans up the
  workspace and LFTP processes before surfacing them.
- Completed, failed, cancelled, and missed jobs now enter a bounded durable
  Activity history. The Agent validates and atomically persists terminal
  records, replays them on reconnect, publishes live updates, and backfills
  terminal durable jobs after an Agent restart without duplicating entries.
- Remote-to-remote route reviews now receive Agent-issued, single-use plan
  identifiers that also become durable job identifiers. Concurrent duplicate
  submissions and lost-reply reconciliation reuse that exact identifier, so
  they converge on one job and one execution instead of launching a second
  transfer under a fresh review. Each review also pins both complete connection
  identities; changing either endpoint, protocol, user, authentication mode, or
  SSH key invalidates the old route before any remote stat or process launch.
- Ordinary transfers now use their reviewed plan identifier as the durable job
  identifier, and mirror approvals consume the reviewed preview identifier in
  the same way. The App retains unresolved submissions across workspace
  rebuilds and reconciles only that exact identifier, while the Agent records
  terminal validation and launch failures so bounded replay caches cannot make
  an old request executable again.
- Mirror approval now round-trips a deterministic fingerprint of the exact
  preview metadata and action list displayed by the App. The Agent compares it
  with its stored HMAC-authenticated dry run before consumption, and expired
  reviews reach the Agent for an authoritative rejection rather than leaving
  the App in an unresolved state.
- Native LFTP transfer queues, segmented downloads, rate controls, safe mirror
  previews/execution, cancellation, and run-once transfer scheduling.
- Native queued file transfers now report live bytes, total size, percentage,
  and transfer rate from bounded LFTP `jobs -vv` status. Status observations
  are matched only to one exact pending source and transfer mode; ambiguous or
  malformed output is ignored without affecting queue completion or safety.
- Fresh SFTP path checks now recognize LFTP's exact path-bound
  `Access failed: No such file (/path)` diagnostic. Unbound, mismatched, or
  multi-path diagnostics still fail closed, while legitimate creation of a new
  SFTP directory or transfer target no longer reports ambiguous server output.
- Per-session non-modal recursive remote name search with depth and case
  controls, literal Unicode basename matching, cancellable isolated LFTP
  processes, bounded snapshot paging, and fresh navigation from results.
  Search output is root-contained and fail-closed; delayed `find` output never
  shares the persistent browse session.
- Reusable package-scoped mirror definitions with native save/select/edit/delete
  controls, bounded validated persistence, profile-aware cleanup, and
  lost-reply reconciliation. Durable definitions never contain preview output,
  approval state, deletion consent, or executable commands; every run still
  requires a fresh Agent-held dry run and deletion review when applicable.
- Typed regular-file and directory transfers. Folder downloads use non-pruning
  LFTP mirrors and uploads use reverse mirrors with no source/extraneous-target
  deletion options; nested links are skipped and changed plain files use
  temporary-file replacement without timestamped backup debris or in-place
  writes through existing hard links. No-clobber jobs temporarily disable that
  replacement mode so Skip remains race-safe. Source and destination kinds are
  revalidated before immediate, scheduled, and retried work, and an exact fresh
  dry run redirects any deletion or type-collision replacement to the reviewed
  Mirror workflow. Root-wide folder jobs also require that reviewed workflow.
- Case-exact mirror deletion review, including distinct names in Windows
  case-sensitive directories, with a second matching dry run and endpoint
  validation immediately before every execution.
- Provenance-safe dual-pane drag/drop using bounded, expiring in-process tokens.
  Internal drops are limited to the opposite pane of the originating session,
  Explorer storage items are accepted only as upload sources, and plain text is
  never interpreted as a transfer request.
- Explicit Activity Center retry for failed transfers, with atomic
  failure-state reset, fresh path/session and skip-policy validation, bounded
  Agent-owned retry details, and duplicate-submission protection. Mirror and
  remote-to-remote jobs continue to require fresh review instead of reusing an
  earlier approval.
- Managed remote editing with Agent-owned cache paths, durable Active Edits
  state across App reconnects, debounced save monitoring, strong local and
  remote content identities, and explicit conflict review. Managed files open
  only in trusted System32 Notepad without shell or file-association execution.
- Remote-edit uploads use an opaque sibling staging file, content verification,
  a fresh target-identity check, reviewed backup/promotion, and fail-closed
  rollback instead of overwriting the live target with `put -e`.
- Self-contained unsigned MSIX validation, App Installer feed generation,
  locked NuGet restores, CycloneDX SBOM generation, offline signing policy, and
  fail-closed third-party source/license publication gates.
- Public-repository build provenance now verifies through GitHub's documented
  Sigstore Public Good trust path and requires a verified transparency-log
  timestamp. GitHub CLI stderr is kept separate from JSON evidence so the same
  fail-closed verification works under Windows PowerShell 5 and PowerShell 7.
