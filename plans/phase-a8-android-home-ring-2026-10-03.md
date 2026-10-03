# A-8: the Android home ring shows the mascot and is a tap target (tester report on r20, item 1)

## Why

The tester saw no change of the home screen on the phone: the Windows home screen got the mascot emblem and a dominant Connect (D-16), the Android one kept a grey ring with a power icon inside a bordered card. The owner added: the circle must be tappable with a finger.

## What

`StatusCard` (the hero of the Android home): the ring is 136 dp, shows the mascot on a white disc (the art is dark line work, so the disc is white in both themes; the picture is the same asset and dark-theme handling as the header tile), with a small state
badge (power icon on the state colour) at the bottom; the card border is gone so the hero sits on the page like on the desktop. A tap on the ring raises `Clicked`, wired to the same handler as the Connect/Disconnect button (`OnConnectClicked`); it ignores
taps while connecting, shrinks to 95 % while pressed, and has the accessibility name Connect/Disconnect and the button role. The Connect button stays under the hero.

## Not covered

The connection-card and routing-segment redesign of the desktop home (the Android config row, tunnel mode and autostart cards keep their layout); the Android app has no UI test project, so the check is an APK build plus the emulator.

## Verification

APK (android-x64) built on windows-worker at the exact SHA, installed on the Linux worker emulator (API 34): the home shows the mascot ring with the badge; `adb input tap` on the ring opens the system VPN consent dialog (`com.android.vpndialogs.ConfirmDialog`), the same as the Connect button; CI builds the release APK.
NOT verified: ring colours in the connected/connecting/error states with a real tunnel, dark theme on a device, the Pixel (Android 16).

## Rollback

Revert the PR.

## Outcome

Pending.
