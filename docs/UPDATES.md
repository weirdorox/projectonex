# Lumen updates

Lumen updates itself from **GitHub Releases** of `weirdorox/projectonex`:
Settings › System › Updates (or Quick settings › System) → **Check** → **Download** → **Install and restart**.

> The repository must be **public** - Windows can't download releases of a private repository
> without a login, and Lumen never stores GitHub credentials.

## How a release is made (owner only)

1. Merge what you want to ship (only you approve merges into `main`).
2. Tag the commit: `git tag v0.4.0` (stable) or `git tag v0.4.0-beta.1` (Beta channel) and push the tag.
3. The **release** workflow builds, runs the tests and the start-up/UI smoke test, packages the
   installer, writes `lumen-update.json`, signs it, and creates a **draft** release.
4. Review the draft on GitHub and press **Publish release**. Only then can Lumen see it.
   Stable users only get releases that are not marked pre-release; Beta users get both.

CI build artifacts (`lumen-x64`) are never offered as updates.

### One-time setup: the signing key

Lumen contains the *public* half of an ECDSA P-256 key (`installer/update-public-key.pem`, also
compiled into `UpdateRules.PublicKeyPem`). The private half must be added as the repository secret
**`LUMEN_UPDATE_KEY`** (Settings › Secrets and variables › Actions › New repository secret, paste the
whole PEM). The release workflow refuses to run without it and checks it matches the public key.
Keep a backup of the private key offline; if it is lost, a new key needs a Lumen build that trusts
the new public key (installed manually once).

## What Lumen checks before installing

| Check | Protects against |
|---|---|
| Release metadata (`lumen-update.json`) carries a valid ECDSA signature from Lumen's key | a modified release, a release created by someone else, a compromised download location - a checksum stored next to the file alone would not prove who made it |
| Version in the signed metadata = release tag, and newer than the installed version | replay of old releases, accidental downgrades |
| Installer name, size and SHA-256 match the signed metadata | corrupted or swapped downloads (rejected and deleted) |
| Installer URL is `https://github.com/weirdorox/projectonex/releases/download/…` | downloads from anywhere else |
| Authenticode signature valid *if the installer is code-signed* | tampering, when a certificate is configured |
| Re-verified right before running | changes on disk after the download |

Nothing from a release is executed except the verified installer itself; release notes are shown as
text only.

**Code signing (optional, recommended):** without an Authenticode certificate the installer is
unsigned. Lumen's own signature check still applies. The downloaded file is run by Lumen directly,
so Windows SmartScreen does not show its download warning for it (the same as other self-updating
apps); UAC is never bypassed - an all-users install shows the normal Windows prompt. To sign,
run `scripts/publish.ps1 -CertificateThumbprint …` on a machine with the certificate.

## Installing, restarting and rolling back

- Lumen never installs while a game is running; it never interrupts a game for an update
  (notifications only appear in the dashboard).
- Install: Lumen keeps a verified copy of the **current** version's installer (for rollback), writes
  an update journal, starts the new installer (`/VERYSILENT /LUMENUPDATE=1`) as a separate process
  and exits. The installer replaces the program files (never `%LOCALAPPDATA%\Lumen`: settings, library,
  favorites, collections, profiles, themes, Steam data stay as they are) and starts Lumen again.
- After the restart Lumen compares the running version with the journal: success → "Updated";
  still the old version → "Update failed" with the installer log path; **Retry** is offered.
- Startup validation: the update counts as good after Lumen has run for 60 s. If the new version
  crashes on start, the crash guard opens **safe mode**, which offers **Roll back the update**
  (also `Lumen.Recovery.exe --rollback-update`). Rollback reinstalls the previous version with its
  verified installer.
- Interrupted downloads resume where they stopped (`.partial` + HTTP range); corrupted files are deleted.
- Inno Setup installs are not atomic: if the PC loses power in the middle of an install, run the
  rollback or install the release manually. Lumen does not promise more than that.

## Status

Unit-tested: version ordering, channel selection, drafts ignored, signature verification (valid,
tampered, wrong key), checksum, validation rules (downgrade, size, name, URL), journal reconcile.
**Not yet verified end-to-end:** a real update from one published release to the next must be done
once the repository is public and the signing secret is set (see TESTING.md › Updates).
