# Windows architecture

LFTP Pilot separates the WinUI process from the long-lived Agent. The App owns
presentation and activation routing; the Agent owns durable job state and all
LFTP process trees. `LFTPPilot.Windows` is the Windows boundary used by both.
The MSIX keeps the Agent's self-contained payload under `agent/` and the
authenticated LFTP runtime under `lftp/`, avoiding dependency collisions with
the App while keeping both package trees read-only.

## Security boundaries

- Control and event channels use distinct byte-mode named pipes created with
  `PipeOptions.CurrentUserOnly`. Both sides validate the peer PID against the
  process they launched or connected to before exchanging frames.
- Every Agent/runtime process is assigned to a kill-on-close Job Object. A
  disposed Agent job cannot leave `lftp`, `ssh`, or shell descendants behind.
- Credentials are stored separately from profiles with DPAPI CurrentUser. DPAPI
  entropy contains profile ID, normalized endpoint, user name, and purpose;
  changing identity invalidates the previous credential.
- Packaged state lives under the package family LocalState/LocalCache roots.
  Unpackaged development runs use a separate `%LOCALAPPDATA%\LFTP Pilot\Development`
  root and never probe LFTP Commander locations.
- Each per-profile transfer is submitted to LFTP's native parallel queue through
  a unique, validated alias that contains the complete parenthesized command.
  Exact success/failure markers retire that alias; cancelling one unaddressable
  native queue item retires the owned queue process and fails neighboring jobs
  closed rather than risking an ambiguous transfer state.
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
