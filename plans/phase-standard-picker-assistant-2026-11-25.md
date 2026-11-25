# Restore desktop server categories and simplify DeepSeek setup

The owner reported two regressions on the Simple home in Standard 2.51.1-r1.
Server rows show a protocol icon but lose their visible use-case categories.
Opening DeepSeek shows command entry, three actions, and source diagnostics at
once. The same Avalonia views serve Windows and Omarchy.

Restore the existing localized categories for the current subscription. Keep
server identities, manual selection, keyboard navigation and refresh intact.
Give the assistant one clear primary action, expose troubleshooting and source
analysis on demand, and preserve confirmation, cancellation and undo.

Verification covers grouped rows with repeated labels, retained selection on
refresh, the expanded assistant with and without a pending confirmation,
RU/EN and narrow-window headless screenshots, the Release build and PR tests.
Inspect the two surfaces against the supplied screenshots using synthetic data.
Verify the installed client after a separately authorized release. Revert the
interface and test changes to roll back.

## Implementation

The Simple list presents localized use-case headings while retaining each
subscription row as the selectable item. Rebuilding the subscription pool
keeps the selected feed and row identities. An eligible subscription shows the
command field directly on Home. Ask DeepSeek submits it in one click. A second
click confirms a proposed change. More opens the optional recovery tools.
Undo remains reachable if the employee later disables the subscription.
