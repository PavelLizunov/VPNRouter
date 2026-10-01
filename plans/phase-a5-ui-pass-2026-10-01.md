# A-5: first comprehensive pass over the Android screens

## Why

The owner sent a screenshot from the Pixel: with the "Config / Mode" row open the main screen did not fit and could not
be scrolled to its end, and it was not clear that such rows open and close. The owner asked to click through the whole
interface, find bugs and awkward places, fix them, and carry the useful parts over to the PC version.

## Method

Test-hook builds on the Android 15 emulator (Pixel 6 profile, 1080x2400), every screen opened and every control tapped,
screenshots read one by one; numbers from the `layout` block of the hook state dump where a layout bug was suspected.

## Found and fixed (this PR)

1. Main screen could not scroll to its last cards. The vertical room for the system bars was the ScrollViewer's Padding;
   the scroll extent does not include it, so the last 80 to 200 dp were unreachable (worse after r11, whose padding was
   wrong). It is now a margin of the content. Measured on a 1080x1700 screen: before, max scroll 104 dp of 185 needed;
   after, the last card ends exactly above the bottom margin.
2. The "Config / Mode" row now has a labelled pill ("Change" or "Hide") with a chevron that flips, instead of a grey arrow.
3. The system Back button and gesture left the app from the log viewer, the export and import overlays, the profiles
   overlay, the open menu and the Advanced screens. `MainActivity.OnBackPressed` now closes the topmost layer first.
4. The menu: icons for every item (Lucide, same set), 44 dp rows, and it closes when an action starts, so the feedback
   line and the screens it opens are not hidden behind it.
5. Grey flat action buttons in Settings (Always-on VPN, battery exclusion, check for updates) looked disabled: tonal role
   with a 44 dp hit area.
6. Muted text had a contrast of about 2.6:1 on white. The `TextMuted` tokens are darker (light #667287, dark #8791A6); the
   desktop app shares the tokens and gets the same fix.
7. The "Rules" settings chip led to a page with only a note that the feature is not wired on Android: the chip is hidden.
8. The subscription form had four cramped columns and 30 dp fields. Now two stacked rows and a full-width Add button, all 44 dp or more;
   other small buttons (36 and 40 dp) raised to 44 dp.
9. On Android 15 and newer the on-screen keyboard covered the bottom of the page (the Add form of the Subscribe tab was
   hidden while typing), because the enforced edge-to-edge window no longer shrinks. The page now ends above the keyboard.

## Seen, not fixed yet

- Public tab: the "Settings" expander in the green card is a bare arrow.
- Several screens still show desktop wording on touch ("double-click", "click"); U6 (#438) covers the free-config texts.
- The Subscribe tab keeps a large empty server box above the subscription controls when no subscription exists.
- Dark theme, system font scale 1.3 and landscape were not walked through yet.
- Desktop (VPNRouter.App): only the token contrast change is carried over so far.

## Outcome

Pending.
