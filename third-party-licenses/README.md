# Native and managed redistribution evidence

LFTP Pilot is MIT licensed, but its separately executed MSYS2/LFTP/OpenSSH
runtime and self-contained .NET/Windows App SDK payload contain software under
multiple licenses. A public binary release is
blocked until `build/Test-LicenseEvidence.ps1` verifies an independently
reviewed `licenses-manifest.json` here with `complete: true`.

`build/New-LicenseEvidenceManifest.ps1` reconstructs the manifest and managed
license/package staging from the exact restored graph. Its default output is
incomplete; `-MarkComplete` is used only after reviewing all license mappings,
source-delivery decisions, and direct distribution URLs. The incomplete
template remains a deliberate fail-closed reference for future graph changes.

The schema 3 manifest must contain one native entry for every package in
`build/runtime-lock/lftp-msys2-x64.lock.json`, repeat its exact version,
filename, and SHA-256, bind every packaged license text by SHA-256, and bind a
local corresponding-source archive to both its SHA-256 and direct public HTTPS
URL.

It must also contain one `managedPackages` entry for every dependency derived
from production `packages.lock.json` and `project.assets.json` files, including
the self-contained runtime packs in
`build/runtime-lock/dotnet-runtime-packs.lock.json`. Test-project packages are
forbidden. Each managed entry binds reviewed license text, NuGet's locked
content hash, the exact public distribution archive by its raw SHA-512, and a
written source-code-obligation decision. When that decision says corresponding
source is required, the source
archive and public URL are mandatory too. This template deliberately has no
managed entries, so it must not be substituted for the reviewed manifest.

Put native source archives under `sources/<package>/`, managed source archives
under `sources/managed/`, and reviewed NuGet archives under
`managed-packages/`; those local staging folders are ignored by Git. The release
tool copies only manifest-referenced, hash-verified files into the public
evidence ZIP and excludes every unrelated local file. Do not mark evidence
complete based on guesses or an older dependency graph.

Runtime binaries, downloaded source archives, and private signing material do
not belong in Git. Reviewed license texts and the small evidence manifest do.
