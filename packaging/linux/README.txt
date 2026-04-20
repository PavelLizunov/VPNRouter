VPNRouter v{VERSION} (Linux BETA)
================================

First-time setup:

1. Extract:
     tar -xzf VPNRouter-v{VERSION}-linux.tar.gz
     cd VPNRouter

2. Make launcher executable (tar should preserve this, but just in case):
     chmod +x VPNRouter.sh VPNRouter.App VPNRouter.CLI

3. Run:
     ./VPNRouter.sh

4. Optional — menu entry:
     cp vpnrouter.desktop ~/.local/share/applications/
     cp icon.png ~/.local/share/icons/hicolor/256x256/apps/vpnrouter.png
     update-desktop-database ~/.local/share/applications/


Elevation
---------
VPNRouter runs the sing-box proxy process as root (needed for TUN mode
routing). It uses pkexec to show a GUI password prompt via your desktop
environment's polkit agent (GNOME / KDE / XFCE / Cinnamon all include
one by default).

If pkexec isn't available on your system, grant sing-box the required
capability once:
     sudo setcap cap_net_admin,cap_net_bind_service=+eip ~/.config/vpnrouter/bin/sing-box

Then VPNRouter will launch sing-box without a password prompt.


GNOME users — tray icon
-----------------------
GNOME doesn't show system-tray icons by default. Install the
AppIndicator / KStatusNotifierItem Support extension from
https://extensions.gnome.org/extension/615/appindicator-support/ to see
the VPNRouter tray icon. KDE / XFCE / Cinnamon work out of the box.


sing-box binary
---------------
The first time you Connect, VPNRouter downloads sing-box-linux-amd64
into ~/.config/vpnrouter/bin/. About 25 MB one-time download.


What's not in this BETA
-----------------------
  * Zapret DPI bypass (Windows-only for now — winws.exe via Cygwin)
  * Telegram proxy (Python-embeddable path is Windows-only)
  * systemd service / boot autostart (session autostart via .desktop works)
  * Auto-update (download new tarball manually for now)


Questions / bugs
----------------
https://github.com/{REPO}/issues
