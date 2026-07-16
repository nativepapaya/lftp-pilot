# MSIX distribution and automatic updates

The stable package identity is `LFTPPilot.Desktop`, publisher
`CN=LFTPPilot.Dev`, architecture `x64`, and protocol `lftp-pilot`. Changing the
identity or signing key creates a different app and breaks updates.

CI uses committed NuGet lock files in locked mode, builds an explicitly
unsigned, self-contained MSIX, and validates its manifest and payload. It then
attests the exact staged file named `LFTPPilot.msix` with GitHub artifact build
provenance. The attestation action is pinned by full commit SHA and the workflow
has only `contents: read`, `id-token: write`, and `attestations: write`.
Runtime validation requires the exact committed file set, sizes, SHA-256 values,
embedded inventory, bundle revision, and package metadata; unexpected `lftp/`
entries fail the build. CI never receives the private signing key. A trusted local
publisher downloads an eligible artifact, verifies its GitHub attestation,
revalidates it, signs a staged copy
with the non-exportable CurrentUser certificate, verifies Authenticode, creates
SBOM/checksum/license assets, and publishes a new release. The CycloneDX SBOM
deduplicates the resolved NuGet, .NET, Windows App SDK/WinUI, and MSYS2
components from production projects only and carries hashes from their lock
evidence. Test dependencies are excluded. The selected self-contained .NET
runtime pack is derived from production `project.assets.json`, checked against
the SDK/RID runtime-pack lock and restored nupkg SHA-512, and included
explicitly. Existing releases are never overwritten.

Production attestation verification is fixed to `nativepapaya/lftp-pilot`,
`.github/workflows/unsigned-package.yml`, `refs/heads/main`, the completely clean
local `HEAD` digest for both source and signer workflow, GitHub's OIDC issuer,
SLSA provenance v1, and GitHub-hosted runners. Public-good attestations and
self-hosted runners are rejected. `Sign-Release.ps1` and
`Publish-Release.ps1` expose no bypass. `Test-BuildProvenance.ps1` has a
diagnostic bypass that works only with a small specially named fixture under the
operating-system temp directory and an exact test-only environment marker; it
cannot validate a production filename.

Signing may add exactly one non-empty `AppxSignature.p7x` ZIP entry. Every other
decoded entry name, length, and SHA-256 must remain byte-for-byte identical to
the verified unsigned MSIX; additions, removals, case changes, substitutions,
and duplicate logical paths fail. The release includes
`LFTPPilot.provenance.json`, binding the full unsigned file SHA-256 and size,
GitHub verification result, source digest and workflow policy, signed file
SHA-256 and size, signature-entry SHA-256, payload-entry count, and release
certificate SHA-256.

The reviewed `1.0` product prefix is combined with the repository-wide workflow
run number using base 65536 (`1.0.<build>.<revision>`). Every MSIX part remains
in range and the result stays monotonic across revision rollover. Changing the
product prefix requires code review rather than an untrusted workflow input.

`LFTPPilot.appinstaller` points to GitHub Releases `latest/download` assets and
uses these Package Pilot-style settings:

- check no more often than every 24 hours on launch;
- no launch prompt and no activation block;
- Windows automatic background update task enabled.

The app does not replace its own executable. If work is active, the App and
Agent continue until idle; Windows can apply a staged package after they exit.

## Local trusted-tester setup

1. Run `build/Initialize-DevCertificate.ps1` once and retain its thumbprint.
   The 3072-bit RSACng private key is non-exportable, and the public certificate is placed in
   the publisher's CurrentUser TrustedPeople store so both verification tools
   can validate candidates. Back up the machine appropriately because losing
   the key ends this update identity.
2. Trust only the exported public `LFTPPilot.cer` on tester machines.
3. Enable GitHub immutable releases for `nativepapaya/lftp-pilot`. Publication
   queries the immutable-release API and fails unless it reports enabled.
4. Sign the exact attested unsigned candidate with `build/Sign-Release.ps1` from
   a clean checkout of its attested `main` commit. The first release additionally
   requires `-ApproveInitialReleaseCertificate` after explicit fingerprint
   review. Later releases download `LFTPPilot.cer` from the latest immutable
   release and require the exact same certificate.
5. Copy `third-party-licenses/licenses-manifest.template.json` to
   `third-party-licenses/licenses-manifest.json`, add the exact evidence for
   every native and managed production dependency. This includes .NET, WinUI,
   Windows App SDK, WebView2, exact NuGet redistribution archives, the selected
   self-contained runtime pack, reviewed license files, and explicit source-code
   obligation decisions. Native corresponding source remains mandatory. Have
   the result independently reviewed before setting `complete` to `true`. The
   committed template is deliberately incomplete and blocks binary release.
6. Create and push the reviewed `v<four-part-version>` tag. Publication peels
   lightweight or annotated tags through the GitHub API and requires the remote
   tag to resolve exactly to the attested source digest. It refuses to replace
   an existing release.
7. Run `build/Publish-Release.ps1` with both `-AttestedUnsignedMsix` and
   `-SignedMsix`, first with `-WhatIf`; review the staged assets, then explicitly
   run it without `-WhatIf`. The script validates the signed payload,
   signer certificate validity/EKU, package identity, version, architecture,
   locked runtime, production-only NuGet graph, selected self-contained runtime
   packs, exact source/license staging set, immutable-release setting,
   provenance, full payload equality, and certificate continuity before
   uploading anything. The release target is the attested source digest rather
   than a moving branch name.

The signing and publish scripts are intentionally not run by CI. Publicly
trusted signing or Microsoft Store distribution is a later product decision.
