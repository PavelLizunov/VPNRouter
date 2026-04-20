# VPNRouter APT Repository

Public apt repository for VPNRouter, hosted on GitHub Pages at:

    https://pavellizunov.github.io/VPNRouter/apt/

Packages are `.deb` artifacts from GitHub Releases, re-indexed and
GPG-signed on every new release by the
[`.github/workflows/publish-apt.yml`](../../.github/workflows/publish-apt.yml)
workflow.

## Usage

### For users: add the repo once

```bash
# 1. Fetch the signing key and store it in the trusted keyring dir.
curl -fsSL https://pavellizunov.github.io/VPNRouter/apt/key.gpg \
    | sudo tee /etc/apt/keyrings/vpnrouter.gpg > /dev/null

# 2. Add the repo to apt sources.
echo "deb [signed-by=/etc/apt/keyrings/vpnrouter.gpg] https://pavellizunov.github.io/VPNRouter/apt stable main" \
    | sudo tee /etc/apt/sources.list.d/vpnrouter.list

# 3. Install.
sudo apt update
sudo apt install vpnrouter
```

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

### Enabling GitHub Pages

Once the publish workflow has created the `gh-pages` branch, go to:

    Settings → Pages → Source: `Deploy from a branch` → Branch: `gh-pages` / `/ (root)`

After the first workflow run the files will be served at
`https://pavellizunov.github.io/VPNRouter/apt/`.

### Re-syncing the whole repo

If anything goes wrong and you need to rebuild the apt repo from
scratch:

```bash
gh workflow run publish-apt --ref main
# (or with a specific tag to publish just one release)
gh workflow run publish-apt --ref main -f tag=v2.22.2
```

This re-downloads every `.deb` artifact from the last 30 releases
(when no tag given) and re-indexes the apt repo.

### Key rotation

If the GPG key is compromised or expired:

1. Generate a new key, update the `distributions` `SignWith:` line.
2. Update the `APT_SIGNING_KEY` and `APT_SIGNING_KEY_ID` secrets.
3. Replace `vpnrouter-apt-public.asc` in this directory with the
   new public block.
4. Re-run the publish workflow — users will see the signature check
   fail on next `apt update` until they re-fetch `key.gpg`. Document
   in the release notes.
