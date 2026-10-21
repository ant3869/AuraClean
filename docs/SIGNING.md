# Code signing plan

**Status: the release candidates are NOT Authenticode-signed.** No certificate exists for this project, nothing in the repository is a certificate or key, and no build claims a publisher identity. An unsigned build is not "trusted" by Windows; see the expected prompts below.

## Why sign

AuraClean installs a program that always runs elevated and edits the system. Windows judges that by the publisher identity:

| | Unsigned (today) | Signed (goal) |
|---|---|---|
| UAC prompt (MSI install, every app launch) | "Unknown publisher" | Shows the verified publisher name |
| SmartScreen on a downloaded file | "Windows protected your PC" (Unknown publisher) | Publisher shown; may still warn until reputation builds |
| Enterprise policies (AppLocker/WDAC, Smart App Control) | Usually blocked | Allowed if the publisher/certificate is trusted |
| Tampering evidence | SHA-256 checksum only | Signature breaks if the file is altered |

A signature is **necessary but not sufficient** for a quiet SmartScreen: reputation is also built from download history, and no certificate type promises an instant clean pass. Treat that as expected early-release friction and keep the "Run anyway" instructions in the release notes.

## What needs signing

1. `AuraClean.exe` (signed **before** it is wrapped, so the MSI payload and the portable EXE are the same signed bytes).
2. The `.msi`.

(`SHA256SUMS.txt` is regenerated after signing, since signing changes the files.)

## Options, in the order I would evaluate them

1. **A cloud/managed signing service** (e.g. Microsoft Trusted Signing, SignPath, or a CA's cloud HSM such as DigiCert KeyLocker). Since mid-2023 the CA/Browser Forum requires code-signing private keys to live on hardware/HSM, which is why a downloadable `.pfx` secret is no longer the normal route for *new* OV/EV certificates. Cloud signing keeps the key off CI runners and works from GitHub Actions. Eligibility, price and identity-validation rules change; **verify current terms with the vendor before committing**, and note that identity validation needs a real person or legal entity as publisher.
2. **An OV or EV certificate on a hardware token or cloud HSM.** EV costs more and has stricter validation; do not assume it removes SmartScreen warnings.
3. **An existing certificate in a `.pfx`** (for example an organisation's internal code-signing cert for managed machines). This is what the current build hooks support.

For an internal/test audience only, an **internal CA certificate** distributed to the test machines is enough to exercise the signed flow; it will *not* be trusted by anyone else.

## How the pipeline is prepared for signing

- `build/Build-Release.ps1` signs the EXE, rebuilds/wraps it into the MSI, then signs the MSI **if** `SIGN_CERT_PFX_PATH` and `SIGN_CERT_PASSWORD` are set in the environment and `signtool.exe` is available (`SIGN_TIMESTAMP_URL` overrides the RFC 3161 timestamp server). SHA-256 file digest and timestamp digest are used. Without those variables it builds unsigned and prints a warning.
- `.github/workflows/release.yml` decodes `SIGN_CERT_PFX_BASE64` into the runner's temp folder **only if** that repository secret exists, signs, then deletes the file. Nothing is committed; `.gitignore` excludes `*.pfx`, `*.p12`, `*.pvk`, `*.snk`, and a test (`ReleaseMetadataTests`) fails if such a file is added.
- To move to a cloud signing service, replace the body of `Invoke-Sign` in `Build-Release.ps1` (the call sites and ordering stay the same).
- `build/Test-Release.ps1` reports the signature status of both artifacts and checks that the release notes say "unsigned" when the artifacts are unsigned.

## Secrets rules

- Never commit certificates, private keys, passwords or tokens; never print them in logs.
- Store them as GitHub **encrypted secrets** (or the signing service's own identity federation), scoped to the release workflow/environment, with required reviewers for the signing job.
- Rotate on suspicion of exposure; revoke the certificate if the key leaks.

## Checklist to ship the first signed release

1. Decide the publisher identity (person/organisation) and pick a signing option above.
2. Obtain the certificate/service account and complete identity validation.
3. Add the secrets/identity to GitHub; run the workflow; confirm `Get-AuthenticodeSignature` reports `Valid` for the MSI and EXE and the timestamp is present.
4. Repeat the manual install/upgrade/uninstall checks on the signed MSI and record the real UAC/SmartScreen behaviour in the release notes (replace the "unsigned" text).
5. Keep the same certificate subject for later releases so reputation and enterprise trust accumulate.
