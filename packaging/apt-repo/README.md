# VPNRouter APT Repository

Public apt repository for VPNRouter, hosted on GitHub Pages at:

    https://pavellizunov.github.io/VPNRouter/apt/

Packages are `.deb` artifacts from GitHub Releases, re-indexed and
GPG-signed on every new release by the
[`.github/workflows/publish-pages.yml`](../../.github/workflows/publish-pages.yml)
workflow.

## Usage

### For users: add the repo once

```bash
# 1. Fetch the signing key (served in binary/dearmored form).
sudo mkdir -p /etc/apt/keyrings
curl -fsSL https://pavellizunov.github.io/VPNRouter/apt/key.gpg \
    | sudo tee /etc/apt/keyrings/vpnrouter.gpg > /dev/null

# 2. Add the repo to apt sources.
echo "deb [signed-by=/etc/apt/keyrings/vpnrouter.gpg] https://pavellizunov.github.io/VPNRouter/apt stable main" \
    | sudo tee /etc/apt/sources.list.d/vpnrouter.list

# 3. Install.
sudo apt update
sudo apt install vpnrouter
```

If you previously fetched `key.gpg` in armored form and got
`NO_PUBKEY D7D4CD7C2AFC8FF0`, re-run step 1 above — the keyring
file will be overwritten with the correct binary key.

After this, `sudo apt upgrade` keeps VPNRouter current along with
the rest of your system.

### To uninstall the repo

```bash
sudo rm /etc/apt/sources.list.d/vpnrouter.list /etc/apt/keyrings/vpnrouter.gpg
sudo apt update
```

## Maintainer info

### Files in this directory

- `distributions` — reprepro configuration (architecture, codename,
  signing key fingerprint).
- `vpnrouter-apt-public.asc` — ASCII-armored public key, published
  to the repo as `key.gpg` so users can `curl` it.

### GitHub secrets required

- `APT_SIGNING_KEY` — ASCII-armored private key matching the public
  key above. Used by the publish workflow to sign `Release` files.
- `APT_SIGNING_KEY_ID` — `6ED816C8E249EE03` (fingerprint of the key).

### GitHub Pages publication

Use GitHub Actions as the Pages publishing source. Keep the configured custom
domain `vpn.ninitux.com` and HTTPS enforcement. The `github-pages` environment
must admit canonical release tags; its deploy job requires `pages: write` and
`id-token: write`. The publisher uploads a verified site artifact and deploys it
without creating or updating a branch.

Manual reindexing must run at an immutable canonical release tag whose source
contains `publish-pages.yml`:

```bash
gh workflow run publish-pages.yml --ref TAG -f tag=TAG
```

The run verifies the tag/SHA and reindexes only the latest published canonical
stable release. It validates the DEB/sidecar, package metadata and signed indexes;
it never adds a candidate DEB or falls back from a malformed latest stable.

### One-time migration from the retired branch publisher

`migrate-pages.yml` is a manual, main-only bridge for the exact existing Pages
snapshot `93abe24c28bef534d6bfe0f0f211c2fb9600f48f`. It verifies 37 files and
56800902 bytes plus existing APT signatures and hashes, then deploys the existing
site without signing, building a product or changing release assets. Its only
hidden file is the empty `.nojekyll` marker, which the Pages artifact action omits;
Actions publication does not run Jekyll.

Before running the migration, enable the workflow Pages source and allow `main`
in the `github-pages` environment, preserving the custom domain and HTTPS. Keep
`gh-pages` until deployment and public installer/help/Android/APT checks pass.
Verify that the replacement has a different workflow ID from the retired
`publish-apt.yml` (historical ID `263772941`) and that no legacy run is active.
GitHub marks the removed workflow as `deleted` and refuses disabling it; this
state alone is not a documented rerun guard. Before deleting `gh-pages`, require
an active creation-only ruleset matching exactly `refs/heads/gh-pages`, with no
bypass actors, and verify its effective rules. Other branch names remain allowed.
After successful migration, disable `migrate-pages.yml` and remove the obsolete
`gh-pages` environment policy. Do not move old tags or store a generated
site/package snapshot in the source branches.

### Key rotation

If the GPG key is compromised or expired:

1. Generate a new key, update the `distributions` `SignWith:` line.
2. Update the `APT_SIGNING_KEY` and `APT_SIGNING_KEY_ID` secrets.
3. Replace `vpnrouter-apt-public.asc` in this directory with the
   new public block.
4. Re-run the publish workflow — users will see the signature check
   fail on next `apt update` until they re-fetch `key.gpg`. Document
   in the release notes.
