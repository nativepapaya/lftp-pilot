# Windows architecture

LFTP Pilot separates the WinUI process from the long-lived Agent. The App owns
presentation and activation routing; the Agent owns durable job and
credential-free session-tab state plus all LFTP process trees.
Job and tab updates share one atomic, revision-ordered writer so an older
asynchronous job capture cannot replace newer jobs or adjacent tab state.
`LFTPPilot.Windows` is the Windows boundary used by both.
The MSIX keeps the Agent's self-contained payload under `agent/` and the
authenticated LFTP runtime under `lftp/`, avoiding dependency collisions with
the App while keeping both package trees read-only.

## Security boundaries

- Control and event channels use distinct byte-mode named pipes created with
  `PipeOptions.CurrentUserOnly`. Both sides validate the peer PID against the
  process they launched or connected to before exchanging frames. Cancellation
  after request I/O begins, malformed framing, or a mismatched correlation ID
  discards the control connection before another request. Agent shutdown first
  closes client admission and drains a stable handler set, then disposes the
  scheduler and workspace.
- Every Agent/runtime process is assigned to a kill-on-close Job Object. A
  disposed Agent job cannot leave `lftp`, `ssh`, or shell descendants behind.
- Credentials are stored separately from profiles with DPAPI CurrentUser. DPAPI
  entropy contains profile ID, normalized endpoint, user name, and purpose;
  changing identity invalidates the previous credential.
- SFTP host trust is public metadata, stored separately from credentials and
  bound to a profile plus canonical endpoint. A credential-free OpenSSH
  process may write exactly one proposed key to an isolated temporary
  `known_hosts` file. The App receives the endpoint identity, algorithm,
  fingerprints, review identifiers, and an opaque approval token, but never the
  raw public key. Approved sessions use an Agent-materialized one-key file, an
  opaque `HostKeyAlias`, `StrictHostKeyChecking=yes`, no global host files, no
  automatic key updates, and `sftp:auto-confirm=false`. Profile,
  trust, session-removal, and job-admission transitions are serialized through
  complete LFTP process cleanup, so a changed key cannot replace trust while an
  old-key process is still exiting.
- Packaged state lives under the package family LocalState/LocalCache roots.
  Unpackaged development runs use a separate `%LOCALAPPDATA%\LFTP Pilot\Development`
  root and never probe LFTP Commander locations.
- Each per-profile transfer is submitted to LFTP's native parallel queue through
  a unique, validated alias that contains the complete parenthesized command.
  Exact success/failure markers retire that alias; cancelling one unaddressable
  native queue item retires the owned queue process and fails neighboring jobs
  closed rather than risking an ambiguous transfer state.
- Ordinary transfer plan IDs and approved mirror preview IDs become their
  durable job IDs. The Agent serializes first submission, caches the terminal
  result, and leaves a failed or missed job tombstone when validation,
  scheduling, or process admission cannot complete. The App keeps an uncertain
  request private across bootstrap rebuilds and may reconcile it only once with
  that same ID; a new plan or preview cannot substitute for an unresolved one.
  Mirror approvals additionally carry a deterministic fingerprint of the exact
  preview envelope and ordered action list the App displayed. The Agent compares
  it to the stored preview before consumption, while its HMAC and fresh second
  dry run remain the execution-side authority.
- Saved mirror definitions use a separate bounded, atomic package-scoped store
  containing only validated planning inputs. Preview output, review
  fingerprints, approval tokens, deletion consent, generated commands, and
  execution state never enter that store. Definition and profile mutations
  share the Agent's profile/trust gate with preview approval, revoke affected
  unconsumed previews, and remove a profile's definitions before deleting its
  metadata so an orphaned definition cannot later be rebound to a recreated
  endpoint.
- Remote editing accepts only a session and canonical remote file path; the
  Agent chooses the package-scoped cache path. Reviews bind strong local and
  remote identities, while dirty/watcher-failure state is returned in bootstrap
  snapshots so closing and reconnecting the App cannot hide a pending save.
  Sessions and profiles with active edits cannot be removed accidentally.
- A reviewed remote-edit upload is written to an opaque sibling, downloaded and
  hashed for verification, and checked again against the reviewed live target
  before backup and rename-based promotion. Rollback preserves a backup or
  quarantined concurrent version rather than deleting ambiguous remote data;
  the live path is never overwritten with `put -e`.
- Remote-to-remote FTP-family transfers use one LFTP process that prefers FXP
  and can fall back to LFTP's client relay. SFTP and mixed-protocol routes use
  two sequential, endpoint-specific LFTP processes and an Agent-owned managed
  payload, so process-global `sftp:connect-program` state and pinned host-key
  files are never shared across endpoints. The payload is freshly size-checked,
  rejected if it becomes a link or special entry, and removed before terminal
  completion on success, failure, or cancellation. Route plans are bounded,
  expiring Agent-issued capabilities whose plan
  ID becomes the durable job ID. The Agent serializes first consumption and
  caches its result, while the App privately reconciles an uncertain submission
  with the same ID; duplicate control-pipe deliveries therefore cannot start a
  second process. Issuance also captures both complete `ConnectionIdentity`
  values, which must still match the active sessions before any path validation
  or transfer process can begin.
- `lftp-pilot://` accepts only `open-profile?id=<guid>`, `transfers`, and
  `settings`. Credentials, commands, paths, and arbitrary mutations are rejected.

## Windows integrations

The Windows library exposes App Installer status, protocol activation, native
notifications, Jump Lists, taskbar progress, package data paths, atomic JSON
stores, sanitized support bundles, and the trusted editor launcher. Managed
remote files are passed as one argument to the fixed System32 Notepad executable
with shell execution disabled; file associations never receive the cache path.
Update downloads and replacement remain owned by Windows App Installer; LFTP
Pilot only reports status and opens the stable feed URI.
