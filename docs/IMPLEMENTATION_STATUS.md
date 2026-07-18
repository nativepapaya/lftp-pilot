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
- Non-modal recursive remote name search in every connected session tab. Each
  search is an isolated, cancellable LFTP `find` process so delayed output
  cannot contaminate the persistent browse session. Literal basename matching,
  canonical root containment, depth/output/result limits, retained snapshot
  paging, and fresh navigation keep large or hostile listings bounded.
- A long-lived, single-instance Agent with bounded versioned JSON frames over
  separate current-user control/event pipes, kernel peer-PID checks, durable
  job snapshots, reconnect/resynchronization, and kill-on-close Job Objects.
  Its atomic state writer validates job snapshots at the same boundary as the
  in-memory coordinator, rejects stale asynchronous captures, and completes
  workspace/process cleanup before reporting a shutdown persistence failure.
- The Agent owns a native notification-area surface while it remains alive.
  Double-click and Open reactivate the allowlisted Transfers view; its explicit
  Stop Agent and transfers command performs normal Agent disposal and
  kill-on-close process-tree cleanup. Explorer restarts re-register the icon,
  and notification callback failures cannot unwind Agent work. Scheduled jobs,
  queued or active transfers, paused work, and managed edits suppress idle
  exit. After the final App disconnects and all such work finishes, the Agent
  keeps a two-minute recovery window before exiting automatically. The same
  grace period cleans up an orphaned, idle Agent after workspace restoration.
- Credential-free session-tab intent persists in the same bounded, atomic
  Agent state. After an Agent restart, tabs return disconnected with stable
  identifiers, ordering, and last local/remote paths; reconnect is an explicit
  user action and reuses the same tab. Missing or identity-changed profiles are
  pruned before any host-trust check, credential lookup, or network process can
  start. A committed close cannot be resurrected by later state writes even if
  LFTP process cleanup initially fails.
- Profile metadata stored separately from DPAPI credentials bound to the exact
  profile, transport endpoint, user name, and authentication purpose. A change
  to protocol, endpoint, user, authentication mode, or SSH key requires
  quiescence and invalidates the old credential identity. Package-scoped paths
  never probe or migrate old product data.
- Explicit SFTP host-key enrollment and changed-key review. Credential-free
  OpenSSH probes record a proposed key in an isolated temporary file; only the
  algorithm and SHA-256 fingerprint cross into the App for review. Approved
  trust is bound to the profile and canonical endpoint. Every allowed SFTP
  browse, transfer, console, mirror, and edit session uses a private one-entry
  `known_hosts` file with `StrictHostKeyChecking=yes`, while LFTP automatic host
  confirmation and OpenSSH global trust/update sources are disabled.
  Changed-key replacement requires explicit review and no active or scheduled
  dependent work. The controlled SSH lab verifies first-use enrollment,
  unchanged trust, a live host-key rotation on the same endpoint, explicit
  replacement, and the restored trusted state through the packaged
  `ssh.exe` probe.
- SFTP key authentication supports both unencrypted and encrypted OpenSSH
  private keys. Optional passphrases travel through LFTP's in-memory credential
  channel, are included in output redaction, never enter process arguments or
  environment variables, and may be stored with the same identity-bound DPAPI
  protection as passwords. The real-runtime lab exercises browse, mutation,
  queued upload, and segmented download with both key forms.
- Authenticated packaged LFTP/MSYS2 runtime execution from a read-only MSIX,
  with an exact file inventory, `--norc`, isolated homes/caches, secret
  redaction, and no credential-bearing process arguments.
- A repository-contained controlled protocol lab exercises the real
  Agent/Engine/runtime path against separate loopback FTP, opportunistic FTP
  TLS, FTPES, implicit FTPS, and SFTP servers. Password authentication, strict
  TLS and SFTP host trust, Unicode browse/mutation, upload, segmented download,
  move, delete, and process cleanup pass across all five endpoints without
  installing a system service or changing the user's certificate store. Its
  exact test-only Python dependency graph is version- and hash-pinned.
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
- Queued `get`, `put`, and segmented `pget` file transfers publish live byte,
  total, percentage, and rate observations from LFTP's own bounded `jobs -vv`
  status. The Agent correlates status only when one pending transfer has the
  exact source path, segmented mode, and validated total; ambiguous or malformed
  output is ignored and progress observers cannot unwind queue execution.
- Fresh mirror dry runs, bounded case-exact structured action parsing, explicit
  deletion approval, definition binding, expiry, and an immediate matching
  second dry run plus endpoint validation before every execution. Dry-run
  script text is never executed. Transfer plan IDs and approved mirror preview
  IDs are also their durable job IDs. Duplicate deliveries converge on one
  result, terminal pre-launch failures remain consumed, and the App blocks new
  work of the affected type while it reconciles a lost reply with the exact
  original ID. A deterministic fingerprint binds the exact ordered action list
  displayed by the App to the Agent-held preview before approval is consumed.
- Reusable mirror definitions persist as bounded package-scoped data and are
  selected, edited, saved, or deleted from the native Mirror surface. They
  contain only validated planning inputs: previews, action lists, approval
  tokens, deletion consent, fingerprints, and generated commands are never
  durable. Definition and profile mutations revoke affected unconsumed
  previews, and lost mutation replies block new mirror work until a fresh
  Agent bootstrap reconciles the stable definition identifier.
- Read-only isolated advanced console; local shell syntax and structured
  mutation/transfer commands are blocked.
- Reviewed FTP-family remote-to-remote file plans and jobs prefer FXP with
  LFTP's client-relay fallback. SFTP and mixed pairs use a managed client relay
  with distinct source and destination processes, independently pinned host
  trust, a freshly size-checked Agent-owned payload, destination no-clobber
  revalidation, and terminal cleanup on success, failure, or cancellation. The
  Agent issues each reviewed plan ID, consumes it once as the durable job ID,
  and makes concurrent or lost-reply replays converge on one execution. The
  plan pins both full connection identities, so a profile endpoint or user
  change cannot redirect an earlier route review.
- Run-once transfer scheduling while the same Agent remains alive. A stopped or
  restarted Agent marks pending schedules missed and never executes them late.
- Bounded durable Activity history for completed, failed, cancelled, and missed
  jobs. Terminal records are validated, atomically persisted, replayed on App
  reconnect, published as live events, and idempotently backfilled from durable
  job state after an Agent restart.
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

- Complete the controlled matrix for interrupted resume, failed-transfer
  retry, active cancellation, FTP-to-FTP FXP/client-relay routing, and the
  remaining routing cases. Password authentication, both SSH-key modes,
  Unicode browse/mutation, TLS, upload, and segmented download already pass on
  every applicable supported protocol.
- Exercise managed-cache editing, concurrent target changes, staging promotion,
  rollback recovery, and the trusted Notepad boundary against the controlled
  SFTP and FTP-family server matrix.
- Extend live progress to guarded directory transfers, reviewed mirrors, and
  remote-to-remote jobs, and wire notifications, taskbar progress, Jump Lists,
  and support-bundle UI.
- Complete outbound Explorer drag/drop for remote files and add richer folder
  transfer controls such as reusable filters and per-tree parallelism.
- Independently review and stage the exact native and managed third-party
  license/redistribution/corresponding-source evidence, create the trusted-test
  certificate, then validate an
  increasing signed App Installer update on clean tester machines.

Recurring schedules, Windows 10, ARM64, portable ZIP distribution, FISH, HTTP
browsing, and torrent support remain deliberately deferred.
