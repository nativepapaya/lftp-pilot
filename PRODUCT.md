# LFTP Pilot product contract

LFTP Pilot is not a one-to-one rewrite of LFTP Commander. The product is
designed from first principles around a native Windows dual-pane workspace and
the capabilities that make LFTP valuable.

## First-class experience

- Every connection is an independent tab with its own local and remote paths.
- Transfers from all tabs appear in one activity center.
- Upload and download mirror definitions are one-way and explicit.
- Any mirror that can delete destination entries requires a new dry-run preview
  and explicit approval for that exact preview.
- Advanced LFTP commands use an isolated session. Local shell escapes are
  disabled unless the user deliberately enables unsafe console mode.
- Active or run-once scheduled work can continue in an optional tray agent.
- Remote-to-remote jobs use FXP when compatible FTP servers permit it and
  otherwise disclose that data will relay through the client.
- Windows App Installer owns updates; update discovery never blocks startup or
  interrupts active work.

## Version 1.0 boundaries

Supported protocols are SFTP, FTP with opportunistic TLS, FTPES, implicit
FTPS, and plain FTP. FISH, HTTP browsing, torrents, recurring schedules,
Windows 10, ARM64, portable ZIP distribution, and data migration are deferred.

## Deliberately not inherited

The product does not inherit Electron IPC, a browser renderer, React state,
Chromium tracing, renderer recovery, portable-first storage, custom UI fonts,
OLED-only styling, or compatibility with LFTP Commander settings and profiles.

## Safety invariants

- Credentials never appear in process arguments, logs, activation URIs, or
  support bundles.
- The application never executes mirror dry-run output as a script.
- A timeout retires the affected LFTP session so late output cannot be assigned
  to a later request.
- Every LFTP/SSH/shell process belongs to a kill-on-close Job Object.
- Activation URIs accept only allowlisted actions and opaque profile IDs.
- The update identity, publisher, and signing key remain stable for the life of
  the trusted-test feed.
