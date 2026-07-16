# Locked LFTP runtime

`lftp-msys2-x64.lock.json` is a data-only copy of the reviewed schema-3 lock
from LFTP Commander bundle revision 8. It pins the exact MSYS2 package archive,
detached-signature digest, signer fingerprint, dependency inventory, and CA
source used as the starting runtime for LFTP Pilot. No Git history or
application code was imported.

`lftp-msys2-x64.files.json` is the matching reviewed 98-file curated inventory.
`../Acquire-LftpRuntime.ps1` checks both locks, both archive/signature SHA-256
values, rejects unsafe archive paths, reconstructs only those files, and asserts
the exact inventory digest after extraction. A local refresh additionally uses
a reviewed keyring and `gpgv`; CI may explicitly reuse the already-authenticated
signature evidence because the archive and signature bytes are pinned exactly.
Runtime binaries, package archives, keyrings, and staging folders remain untracked.

`dotnet-runtime-packs.lock.json` binds the .NET SDK, `win-x64` RID, selected
framework reference, exact self-contained runtime-pack version, NuGet content
hash, and SHA-512. Release validation derives the selected pack from production
`project.assets.json`, verifies the restored nupkg bytes, and rejects test
projects or any drift from this lock.

Packaging includes the authenticated manifest and must verify every current
file hash before first execution.
