# LFTP/MSYS2 runtime trust

LFTP remains a separately executed, x64 MSYS2 runtime. The repository does not
commit its binaries. `build/runtime-lock/lftp-msys2-x64.lock.json` pins every
archive and detached signature used by the initial runtime.

Local acquisition requires a separately reviewed MSYS2 keyring. The acquisition
tool checks archive and signature SHA-256 values, authenticates the detached
signature with `gpgv`, checks the exact signer fingerprint, rejects traversal
members, and reconstructs only the committed file inventory in a new staging
directory. CI may use the explicitly named authenticated-lock-evidence mode;
that mode accepts only the already reviewed archive and signature hashes and
still reconstructs the same exact inventory. Acquisition refuses to replace an
existing runtime implicitly.

Packaging must include the byte-exact committed `bundle-manifest.json` and
bundle revision. Both unsigned and signed release gates stream and hash every
runtime entry from the MSIX, verify its exact path and size, and reject missing
or additional `lftp/` entries. Runtime execution redirects `HOME`, `TMP`, `TEMP`,
`known_hosts`, and caches to package data, and smoke-test `lftp.exe`, `ssh.exe`,
and `sh.exe` from the installed package. A package may not weaken these gates to
make a build pass.

Binary publication additionally requires exact license texts and
corresponding-source evidence for every locked package. The deliberately
incomplete template does not authorize redistribution.
