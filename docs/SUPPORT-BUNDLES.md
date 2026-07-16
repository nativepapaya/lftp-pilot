# Support bundles

Support bundles contain bounded UTF-8 JSON, log, text, and Markdown entries
only. The Windows builder rejects rooted/traversal names, entry sizes above 4
MiB, and totals above 32 MiB. It redacts URI user information, password/token
assignments, authorization headers, private-key markers, and the current user
profile path before writing an atomic ZIP.

Callers must still pass only an explicit allowlist of diagnostics. Never add
profile stores, DPAPI blobs, private keys, known-host databases, raw command
input, remote edit caches, or arbitrary user-selected files to a bundle.
