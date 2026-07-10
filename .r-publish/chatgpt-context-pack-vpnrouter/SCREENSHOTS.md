# Screenshot Map

All PNG files are in `screenshots/`.

## Real App Smoke

`real-r35-simple-mode.png`

Real VPNRouter v2.46.0-r35 running on the Windows test VM. Shows the simple first-run screen: status card, config input, split/full choice, connect button, and advanced settings entry.

## Main Window Width Smoke

`mainwindow-300.png`, `mainwindow-360.png`, `mainwindow-440.png`, `mainwindow-520.png`

Narrow-width smoke screenshots. Use these to discuss compact-window overflow, clipped footer buttons, status badges, and whether the UI still feels understandable on small windows.

## Core Pages

`page-simple.png`

Simple mode. The user can paste a config/subscription, choose selected apps vs full traffic, and connect. This is the main screen for non-technical users.

`page-servers.png`

Server list page. Used for manual/server config management and server selection.

`page-subscribe.png`

Subscription page. Used to manage subscription URLs, fetch server pools, and inspect subscription-derived servers.

`page-network.png`

Settings -> Routing. Explains split tunnel vs full tunnel and Russian traffic/direct-routing controls.

`page-network-routing-narrow.png`

Narrow version of the routing settings. Use it to check wrapping and card sizing.

`page-applications.png`

Applications page. This is the important app-routing screen with categories and process entries. App routing has two separate concepts: "Через VPN" and "Мимо VPN".

`page-tools.png`

Tools page. Diagnostic and helper actions live here.

`page-free-configs.png`

Public/free configs page. Used for public config pools and quick connection options.

## Windows-Specific Pages

`page-dpi-bypass.png`

Zapret/DPI bypass page. Windows-only helper for DPI-blocked services.

`page-telegram.png`

Telegram proxy page. Windows-only helper around a Telegram proxy.

`page-telegram-narrow520.png`

Narrow Telegram page. Useful for checking whether long proxy secret/port controls overflow.

`page-telegram-running.png`

Telegram proxy page in running-state banner mode.

## Autostart Settings

`page-network-autostart.png`

Settings -> Autostart. Shows boot/session autostart controls.

`page-network-autostart-no-service.png`

Autostart with Windows Service not installed. Useful for discussing how to explain why boot autostart will not fire.

`page-network-autostart-service-installed.png`

Autostart with service installed and running.

`page-network-autostart-service-installed-stopped.png`

Autostart with service installed but currently stopped. The UX intent is that boot autostart can still be configured because SCM can start it on next boot.

`page-network-autostart-narrow.png`, `page-network-autostart-narrow500.png`, `page-network-autostart-narrow400.png`

Narrow autostart variants for overflow review.
