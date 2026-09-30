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
`-p:VpnRouterTestHook=true` (android-x64) on the Windows worker and a check on the Linux worker's emulator; the result is
recorded below.

## Outcome

Merged after green exact-head CI. See the emulator result below.
