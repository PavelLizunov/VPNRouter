#!/usr/bin/env bash
# VPNRouter launcher — resolves the script's own directory so the tarball
# can be unpacked anywhere (~/VPNRouter, /opt/vpnrouter, etc.) and still
# locate its sibling libraries.
SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
exec "$SCRIPT_DIR/VPNRouter.App" "$@"
