# Changelog

All notable changes to LFTP Pilot will be documented in this file.

The project uses four-part MSIX package versions for monotonic App Installer
updates and human-facing `v1.0.<sequence>` release tags during trusted testing.

## Unreleased

### Added

- Initial native Windows repository, architecture, and product foundation.
- Virtualized WinUI dual panes, persistent Agent sessions, native file
  operations, Quick Access bookmarks, transfer activity, and reviewed remote
  transfer routing.
- Profile-bound DPAPI secrets, authenticated named pipes, kill-on-close process
  trees, isolated read-only console sessions, and exact packaged-runtime trust.
- Native LFTP transfer queues, segmented downloads, rate controls, safe mirror
  previews/execution, cancellation, and run-once transfer scheduling.
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
