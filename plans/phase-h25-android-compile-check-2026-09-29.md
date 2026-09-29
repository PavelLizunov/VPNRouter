# H-25: compile the Android project on pull requests

## Why

Android is built only on `v*` tags (`build-android.yml`), so a pull request that breaks the Android
project or a source-linked Core file stays green until a release tag. H-4b (39 Android methods
removed) and H-23 (Core classes split) both needed a manual compile check on `windows-worker`.

## What

New workflow `android-compile.yml`: on pull requests touching `VPNRouter.Android/**`,
`VPNRouter.Core/**`, `Directory.Build.props` or `global.json`, build the Android project unsigned
(`dotnet build -c Release -p:RuntimeIdentifier=android-arm64`) on a GitHub runner with the same
pinned actions, JDK 17, SDK platform 36 and hash-checked `libbox.aar` as `build-android.yml`. No
secrets, no keystore, read-only permissions; it uploads nothing.

## Not covered

No device tests. It is not a required check until the owner adds it to branch protection. Runtime
is expected around 8 to 10 minutes on runs that touch those paths.

## Verification

The workflow runs on its own pull request (its path filter includes itself).

## Outcome

Merged as #360 on 2026-09-29 by owner decision. The workflow ran on its own pull request (`compile` job passed, about 8 minutes) and it now runs on every pull request that touches `VPNRouter.Android`, `VPNRouter.Core`, `Directory.Build.props` or `global.json`.
