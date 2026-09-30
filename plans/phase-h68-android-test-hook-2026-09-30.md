# H-68: a test-only adb hook for the Android app

## Why

Checking the app on an emulator so far meant tapping by screenshot coordinates and typing long links one chunk at a time
(the emulator drops characters). The app draws its own controls, so `uiautomator` sees no accessibility tree ("null root
node"), and generic Android MCP servers that read that tree see nothing. A tiny receiver that reads the state and accepts a
config over `adb shell am broadcast` removes both problems and makes checks repeatable.

## What

- `TestHookReceiver.cs` (exported `BroadcastReceiver`) and `AndroidApp.TestHook.cs` (UI-side actions that reuse the
  handlers behind the real buttons): `TEST_DUMP_STATE`, `TEST_SET_CONFIG` (`--es value`), `TEST_CONNECT`,
  `TEST_DISCONNECT`, `TEST_RESET`. Each answers with one JSON line in the ordered-broadcast result and in logcat (tag
  `VpnRouterTest`). URLs, uuids and long tokens are replaced by placeholders; the dump reports the active server NAME only
  and presence flags for the share link, subscription URL and custom JSON, never their values.
- Compile-time gate: both files are wholly inside `#if VPNROUTER_TESTHOOK`; the csproj defines it only when a build is
  started with `-p:VpnRouterTestHook=true` (and appends `+testhook` to the versionName). `MainActivity` logs a warning at
  start in such builds.
- `AndroidTestHookGuardTests` (Core test project) fails if a workflow or any other build file mentions the switch, if the
  csproj sets it by itself or defines the symbol without the condition, if a hook source is not wholly inside the gate,
  or if another Android source uses the symbol outside a gate.
- Usage is in `VPNRouter.Android/AGENTS.md`.

Not in this step: an MCP server or any other channel; a hook in release builds; the tunnel itself (needs an arm64 device).

## Verification

CI (`compile` job builds the Android project without the switch, `test` runs the guard tests). Then a hand build with
`-p:VpnRouterTestHook=true` (android-x64) on the Windows worker and a check on the Linux worker's emulator.

Result, head 48c16aec (2026-09-30):

- CI on the exact head: `compile`, `test` (the five guard tests), `grep`, `characterization-windows`, `go-test-windows` green.
- Windows worker build of the head with `-p:VpnRouterTestHook=true`, android-x64, Release: load preflight freeRAM 13.3 GB,
  cpu 6%, disk free 19.3 GB, no other job; 0 errors, 81 warnings (same count as before, none in the new files), 4 min.
  versionName of the APK: `2.50.0-r10+testhook`.
- Android 14 x86_64 emulator on the Linux worker (booted from the saved snapshot in 11 s):
  - `TEST_DUMP_STATE` answers even before the UI exists (`uiReady:false`) and after start (`uiReady:true`); state
    `disconnected`, mode `manual`, no config, `abis:[x86_64,arm64-v8a]`.
  - `TEST_SET_CONFIG` with a share link -> `kind:share-link, mode:manual`; with an `http` URL -> `kind:subscription-url,
    mode:subscribe`; with `{"outbounds":[]}` -> `kind:custom-json, mode:custom, valid:false`; with `notalink` ->
    `ok:false, error:unsupported-value`.
  - Leak check: a random uuid, the host of a fake link, and a fake subscription token were sent; none of them appears in the
    `VpnRouterTest` log or in any reply (0 hits each).
  - `TEST_CONNECT` (one call) shows the system VPN consent dialog; after OK the dump reads `consentGranted:true` and
    `state:error, reason:"UnsatisfiedLinkError: dlopen failed: library \"libbox.so\" not found"` (libbox is arm64-only, so
    the x86_64 build cannot start the tunnel; this also exercises H-67). A second `TEST_CONNECT` with consent granted ends in
    `state:error, reason:"NoClassDefFoundError: io.nekohasekai.libbox.SetupOptions"`; the process stayed alive and the
    crash buffer has no entry.
  - `TEST_DISCONNECT` and `TEST_RESET` return `ok:true`; the dump afterwards shows `hasShareLink:false`, state `disconnected`.
  - Negative check: the H-67 build (no hook) installed on the same emulator has 0 `TestHookReceiver` entries in its package
    dump, the same broadcast gets no answer and no `VpnRouterTest` log line appears.
- Observation, not investigated: two `TEST_CONNECT` calls a few seconds apart while the consent dialog was still open left
  the state `disconnected` after OK (a single call works). It may be the same behaviour a double tap on Connect has;
  `RequestConnect` was not changed here.
- Not verified: the successful tunnel path (needs an arm64 device), an arm64 hook build, and the hook on Android older than 14.

## Outcome

Merged after green exact-head CI. The hook exists only in hand-built APKs; nothing in the release path changed.
