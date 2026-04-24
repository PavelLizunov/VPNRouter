#!/bin/sh
# VPNRouter one-liner installer for Debian / Ubuntu / Mint and derivatives.
#
# Usage:
#   curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh
#   # or, if the root URL serves this script:
#   curl -fsSL https://vpn.ninitux.com | sudo sh
#
# What it does:
#   1. Verifies the host is a supported Debian-family distribution.
#   2. Installs curl + gnupg if missing (needed to fetch our signing key).
#   3. Adds the VPNRouter apt repository signing key to /etc/apt/keyrings/.
#   4. Registers the repository in /etc/apt/sources.list.d/.
#   5. `apt-get update` + `apt-get install vpnrouter`.
#   6. Prints a short "how to launch" message.
#
# After installation, `sudo apt upgrade` keeps VPNRouter current along
# with the rest of the system — no separate update channel.
#
# Source of truth for this script lives in the VPNRouter repo at
# packaging/apt-repo/install.sh and is published to GitHub Pages on every
# release via .github/workflows/publish-apt.yml.

set -eu

# ─── Colour helpers (disabled if stdout is not a tty, e.g. behind a pipe) ─
if [ -t 1 ] && command -v tput >/dev/null 2>&1 && [ "$(tput colors 2>/dev/null || echo 0)" -ge 8 ]; then
    C_GREEN="$(tput setaf 2)"
    C_RED="$(tput setaf 1)"
    C_YELLOW="$(tput setaf 3)"
    C_CYAN="$(tput setaf 6)"
    C_RESET="$(tput sgr0)"
else
    C_GREEN="" C_RED="" C_YELLOW="" C_CYAN="" C_RESET=""
fi

say() { printf '%s==>%s %s\n' "$C_CYAN" "$C_RESET" "$*"; }
ok()  { printf '%s✓%s %s\n'   "$C_GREEN" "$C_RESET" "$*"; }
warn(){ printf '%s⚠%s %s\n'   "$C_YELLOW" "$C_RESET" "$*" >&2; }
die() { printf '%s✗%s %s\n'   "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

# ─── Root check ──────────────────────────────────────────────────────────
if [ "$(id -u)" -ne 0 ]; then
    die "This installer must run as root. Try: curl -fsSL <url> | sudo sh"
fi

# ─── Distro detection ────────────────────────────────────────────────────
if [ ! -f /etc/os-release ]; then
    die "Cannot detect Linux distribution (no /etc/os-release). Supported: Debian, Ubuntu, Mint, Pop!_OS, elementary."
fi

# shellcheck disable=SC1091
. /etc/os-release

case "${ID:-}" in
    debian|ubuntu|linuxmint|pop|elementary|raspbian|kali|neon)
        ok "Detected: ${PRETTY_NAME:-$ID}"
        ;;
    *)
        # ID_LIKE catches distros that inherit from Debian/Ubuntu
        case " ${ID_LIKE:-} " in
            *" debian "*|*" ubuntu "*)
                warn "Detected ${PRETTY_NAME:-$ID} — not directly tested, but Debian-compatible. Proceeding."
                ;;
            *)
                die "Unsupported distribution: ${PRETTY_NAME:-$ID}. The apt installer only supports Debian / Ubuntu and their derivatives. For Arch / Fedora / etc., use the tar.gz or AppImage from https://github.com/PavelLizunov/VPNRouter/releases/latest"
                ;;
        esac
        ;;
esac

# ─── Prereqs ─────────────────────────────────────────────────────────────
MISSING=""
for tool in curl gpg; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        MISSING="$MISSING $tool"
    fi
done

if [ -n "$MISSING" ]; then
    say "Installing missing prerequisites:$MISSING"
    apt-get update -qq
    # shellcheck disable=SC2086
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq $MISSING
fi

# ─── Add repo ────────────────────────────────────────────────────────────
REPO_URL="https://pavellizunov.github.io/VPNRouter/apt"
KEYRING="/etc/apt/keyrings/vpnrouter.gpg"
SOURCES="/etc/apt/sources.list.d/vpnrouter.list"

say "Adding VPNRouter apt repository"
mkdir -p /etc/apt/keyrings

# Fetch signing key. Previous versions of this script accepted the ASCII-
# armored .asc file; current repo serves a dearmored binary .gpg which is
# what apt expects behind `signed-by=`.
if ! curl -fsSL "$REPO_URL/key.gpg" -o "$KEYRING"; then
    die "Failed to download signing key from $REPO_URL/key.gpg — check your network / firewall."
fi
chmod 0644 "$KEYRING"

printf 'deb [signed-by=%s] %s stable main\n' "$KEYRING" "$REPO_URL" > "$SOURCES"
chmod 0644 "$SOURCES"

# ─── Install ─────────────────────────────────────────────────────────────
say "Updating package index"
apt-get update -qq

say "Installing VPNRouter"
DEBIAN_FRONTEND=noninteractive apt-get install -y vpnrouter

# ─── Verify ──────────────────────────────────────────────────────────────
INSTALLED_VERSION="$(dpkg-query -W -f='${Version}' vpnrouter 2>/dev/null || echo unknown)"

printf '\n'
ok "VPNRouter $INSTALLED_VERSION installed."
printf '\n'
printf '  Launch from terminal:      %svpnrouter%s\n' "$C_CYAN" "$C_RESET"
printf '  Or from application menu:  look for "Virtual Penguin Network"\n'
printf '\n'
printf '  First run will prompt once for a VLESS server / subscription URL.\n'
printf '  Updates arrive automatically via %ssudo apt upgrade%s.\n' "$C_CYAN" "$C_RESET"
printf '\n'
printf '  Passwordless mode: the post-install hook already applied POSIX\n'
printf '  capabilities to sing-box, so VPN start/stop needs no password.\n'
printf '\n'
printf '  Docs + issues:  https://github.com/PavelLizunov/VPNRouter\n'
printf '\n'
