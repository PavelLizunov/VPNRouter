# A-6: Android leftovers of the A-5 pass, landscape

## Why

A-5 (`plans/phase-a5-ui-pass-2026-10-01.md`) listed what it saw but did not fix; the owner asked to finish all design
fixes. A walk of every Advanced screen on the Android 15 emulator (test-hook build of A-5, 1080x2400, portrait and
landscape) found more.

## Found (emulator, before)

1. Public tab: the "Settings" expander in the green card is a bare arrow and a word.
2. Subscribe tab without subscriptions: a large empty server box, "Auto-select", "Test all", "Deep verify" and
   "Refresh all" (all useless without servers), a "Subscriptions" header and "Add a subscription below" above the form.
3. Back inside the inline subscription editor leaves the Advanced screens and the edit is lost.
4. The inline editor: outlined 30 dp fields unlike the add form, Save and Cancel look the same.
5. "Refresh all" is a small outlined button (about 30 dp) next to the 44 dp tonal "Test all" and "Deep verify".
6. Servers tab, built-in Manual list empty: "This subscription has no servers yet. Refresh it" (there is nothing to
   refresh; the user adds links).
7. Public tab status line "Cache is empty - click 'Refresh'": there is no Refresh button on Android or desktop. The
   list hint says "Click the button above" on a touch screen. Saved tab shows its empty hint twice.
8. Landscape: the Servers list collapses to zero height, the Subscribe tab draws rows over each other ("Test all",
   "Auto-select" and "Deep verify" overlap), the Public tab pushes the bottom navigation off the screen. The fixed
   chrome (header, footer, 64 dp navigation) leaves about 120 dp for the page.
9. Checkboxes and radio buttons use the system accent (indigo on the emulator, wallpaper colour on Material You
   phones) instead of the app accent.

## Changes

1. Public "Settings" row: label on the left, a "Show" / "Hide" pill with a chevron that flips on the right (same
   pattern as the main-screen config row); 44 dp. New keys `AdvPublicSettingsShow`, `AdvPublicSettingsHide`.
2. Subscribe tab without subscriptions: an empty state (the navigation's subscription icon, "No subscriptions yet",
   one sentence on what a subscription is and what to do) replaces the server box, the test row and the list header;
   they come back with the first subscription. Keys `SubsIntroTitle`, `SubsIntroBody`.
3. Back closes an open subscription editor or delete confirmation first (`AndroidApp.Back.cs` one line,
   `CloseSubscriptionEditor` in the Subscribe partial).
4. Editor fields look like the add form (filled, 44 dp); Save is the primary (accent) button, Cancel secondary.
5. "Refresh all" gets the tonal role and 44 dp like its neighbours.
6. Manual list empty hint: `SrvManualEmptyHint` ("Paste a server link into the field below and add it").
7. Shared strings: `FcStatusEmpty` is "No configs yet." and `FcSearchListEmptyHint` "Use the button above to find
   configs." (desktop shows both too; neither had a Refresh button). The Saved tab's top hint is removed (the centred
   one stays); "Clear all" gets 44 dp.
8. Landscape and small heights: every Advanced page sits in a vertical ScrollViewer and is laid out at least 520 dp
   high, so lists keep their room and the page scrolls; in portrait on a normal phone nothing changes (the page is
   exactly the viewport). When the keyboard makes a page scroll, the focused field is brought into view. Below a
   600 dp shell height the navigation puts labels next to the icons and is 20 dp lower.
9. Fluent accent palette pinned to the token accent (`AccentSolid`, light #0EA5E9, dark #38BDF8).

Not touched (main thread): `AndroidApp.axaml.cs`, `AndroidApp.KebabMenu.cs`, `MainActivity.cs`.

## Verification

- PR CI: Android compile, test suite.
- Test-hook APK of the exact branch SHA built on the Windows worker; on the Android 15 emulator: Public toggle both
  states, Subscribe empty and with the local 3-server subscription, editor + Back, Manual list empty, Saved tab, light
  and dark, Russian, landscape on every tab, keyboard in portrait on the Subscribe form.

## Outcome

(filled in at delivery)
