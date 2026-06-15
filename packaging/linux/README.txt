VPNRouter v{VERSION} (Linux BETA)
================================

First-time setup:

1. Extract:
     tar -xzf VPNRouter-v{VERSION}-linux.tar.gz
     cd VPNRouter

2. Make launcher executable (tar should preserve this, but just in case):
     chmod +x VPNRouter.sh VPNRouter.App

3. Run:
     ./VPNRouter.sh

4. Optional — menu entry:
     cp vpnrouter.desktop ~/.local/share/applications/
     cp icon.png ~/.local/share/icons/hicolor/256x256/apps/vpnrouter.png
     update-desktop-database ~/.local/share/applications/


Elevation (passwordless since v2.28.0)
--------------------------------------
If you installed via the .deb package or the apt repository, the
post-install hook already applied POSIX capabilities to the bundled
sing-box binary:

    cap_net_admin + cap_net_bind_service

That lets sing-box create a TUN adapter and bind low ports without
running as root, and without any password prompt. Same approach as
tcpdump, wireshark, ping. No pkexec round-trip every time you connect.

tar.gz users: run setcap once to unlock passwordless mode —

     sudo setcap cap_net_admin,cap_net_bind_service=+eip ~/.config/vpnrouter/bin/sing-box

AppImage users: AppImage is a read-only squashfs so setcap on the
embedded binary is impossible. The app detects this and falls back to
pkexec, which pops a GUI password prompt via your desktop polkit agent
(GNOME / KDE / XFCE / Cinnamon all include one by default). Same UX
as VPNRouter v2.21 — v2.27.

If both capability and pkexec are unavailable (headless distro with no
polkit agent), you can still run VPNRouter.App, but Connect will fail
with a clear error. Install a polkit agent (e.g. policykit-1) or apply
setcap manually to proceed.


GNOME users — tray icon
-----------------------
GNOME doesn't show system-tray icons by default. Install the
AppIndicator / KStatusNotifierItem Support extension from
https://extensions.gnome.org/extension/615/appindicator-support/ to see
the VPNRouter tray icon. KDE / XFCE / Cinnamon work out of the box.


sing-box binary
---------------
sing-box-linux-amd64 ships bundled next to VPNRouter.App (and inside the
.deb), so there is no first-connect download — it is provisioned into
~/.config/vpnrouter/bin/ on first run.


What's not in this BETA
-----------------------
  * Zapret DPI bypass (Windows-only for now — winws.exe via Cygwin)
  * Telegram proxy (Python-embeddable path is Windows-only)
  * systemd service / boot autostart — the .deb does NOT install a systemd
    unit; session autostart via the .desktop entry works
  * DNS-leak lockdown / firewall kill-switch (Windows + macOS only; not on Linux)
  * AppImage self-update — the .deb and tar.gz installs auto-update in place;
    the AppImage is read-only, so update it by downloading a new one

NaiveProxy and the DNS-tunnel (Slipstream) transport ARE supported on Linux.


Questions / bugs
----------------
https://github.com/{REPO}/issues
