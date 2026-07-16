# Implementation status

LFTP Pilot is an engineering preview, not a 1.0 release. The repository is a
fresh native Windows codebase with no Git history or data migration from LFTP
Commander. This page separates working foundations from acceptance work that
still needs real servers, signed packages, or additional product development.

## Implemented in the bootstrap

- Native WinUI 3 session tabs and virtualized dual file panes with sorting,
  multi-selection, synchronized resizable columns, keyboard actions, native
  context menus, inbound Explorer drag/drop, and per-profile Quick Access.
  Internal drag/drop uses bounded, expiring opaque tokens and is accepted only
  by the opposite pane in the same session; plain text and cross-session drops
  cannot become transfer requests.
- Structured local and remote create-directory, rename/move, and permanently
  delete operations. Deletion is confirmation-gated, and recursive directory
  deletion has a separate warning and choice.
- A long-lived, single-instance Agent with bounded versioned JSON frames over
  separate current-user control/event pipes, kernel peer-PID checks, durable
  job snapshots, reconnect/resynchronization, and kill-on-close Job Objects.
- Profile metadata stored separately from profile/endpoint/user/purpose-bound
  DPAPI credentials. Package-scoped paths never probe or migrate old product
  data.
- Authenticated packaged LFTP/MSYS2 runtime execution from a read-only MSIX,
  with an exact file inventory, `--norc`, isolated homes/caches, secret
  redaction, and no credential-bearing process arguments.
- LFTP-backed uploads/downloads, resume and skip policies, segmented `pget`,
  rate controls, per-profile native LFTP queues, cancellation-safe queue
  retirement, typed regular-file and directory transfers, explicit
  failed-transfer retry, mirror/reverse-mirror previews, and multi-session
  concurrency. Folder transfers use non-pruning `mirror`/`mirror --reverse`
  commands with no source or extraneous-target deletion options, skip nested
  links, and replace changed plain files through LFTP temporary files without
  timestamped backup debris or in-place hard-link writes. Skip and other
  no-clobber jobs temporarily disable that mode so existing targets remain
  protected. Folder jobs freshly validate endpoint kinds for immediate,
  scheduled, and retried work. Each attempt receives an exact fresh dry run;
  deletion, type-collision replacement, and root-wide jobs are redirected to
  the separately reviewed Mirror workflow. Retry is an Agent-owned, bounded
  operation:
  it revalidates the exact originating session and source/destination policy,
  waits for prior scheduled/active attempt cleanup, prevents duplicate
  submission, and clears stale retry capability after an Agent restart rather
  than persisting executable paths as job history.
- Fresh mirror dry runs, bounded case-exact structured action parsing, explicit
  deletion approval, definition binding, expiry, and an immediate matching
  second dry run plus endpoint validation before every execution. Dry-run
  script text is never executed.
- Read-only isolated advanced console; local shell syntax and structured
  mutation/transfer commands are blocked.
- Reviewed remote-to-remote file plans and jobs. FTP-family pairs prefer FXP
  with LFTP's client-relay fallback; SFTP/mixed pairs clearly require relay.
- Run-once transfer scheduling while the same Agent remains alive. A stopped or
  restarted Agent marks pending schedules missed and never executes them late.
- Managed-cache editing for regular remote files. The Agent alone chooses the
  package-scoped path, watches debounced local saves, and binds review tokens to
  strong remote path/size/mtime/SHA-256 and local SHA-256 identities. Dirty and
  watcher-failure state survives App reconnects through the Active Edits
  surface; active edits block accidental session disconnect or profile removal.
- Managed copies open only in `%SystemRoot%\System32\notepad.exe` with shell
  execution disabled and the complete managed path passed as one argument.
  Upload approval creates an opaque remote sibling, verifies its content,
  freshly revalidates the live target, then uses reviewed backup/promotion and
  fail-closed rollback. Remote editing never applies `put -e` to the live path.
- App Installer update status/UI and stable-feed generation with a quiet
  24-hour on-launch check plus background checks. CI builds and attests unsigned
  packages; local release tooling enforces byte-identical decoded package
  payloads before and after signing, allowing only the non-empty signature
  entry. It also enforces exact LFTP contents, production dependency locks, a
  production-only SBOM, immutable releases, certificate continuity, and a
  non-exportable trusted-test signing key. Managed .NET/WinUI legal evidence is
  deliberately incomplete, so binary publication remains fail-closed.

## Still required before 1.0 acceptance

- Exercise authentication, Unicode, mutation, resume/retry/cancel, TLS, FXP,
  and relay behavior against controlled SFTP and FTP-family servers.
- Add explicit SFTP host-key enrollment/change review and encrypted private-key
  passphrase support.
- Persist and restore session tabs after an Agent restart, not only while the
  background Agent remains alive.
- Exercise managed-cache editing, concurrent target changes, staging promotion,
  rollback recovery, and the trusted Notepad boundary against the controlled
  SFTP and FTP-family server matrix.
- Save reusable mirror definitions and add non-modal recursive remote search.
- Add the tray surface, idle-exit policy, transfer progress/history plumbing,
  and wire notifications, taskbar progress, Jump Lists, and support-bundle UI.
- Complete outbound Explorer drag/drop for remote files and add richer folder
  transfer controls such as reusable filters and per-tree parallelism.
- Independently review and stage the exact native and managed third-party
  license/redistribution/corresponding-source evidence, create the trusted-test
  certificate, then validate an
  increasing signed App Installer update on clean tester machines.

Recurring schedules, Windows 10, ARM64, portable ZIP distribution, FISH, HTTP
browsing, and torrent support remain deliberately deferred.
