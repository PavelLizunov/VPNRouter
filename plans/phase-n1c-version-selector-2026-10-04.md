# N-1c: the version selector reaches the candidates

## Why

The owner asked for a version choice in Settings so that every future version can go back to an older one. It already exists on desktop since the August rollback work (PR #161, Settings > Updates > "Other versions"): the running build plus the three most recent older STABLE releases, SHA-256 verified, a settings backup before the downgrade, the install receipt cleaned so an old client does not read the downgrade as a failed update. Builds before that change cannot go back (only forward), as the owner expected.

What it does not cover is the people who run the experimental channel: a tester on r27 cannot go back to r26 (candidates are filtered out as "not stable"), and that is the case that matters most while releases are rolling candidates.

## What

1. `IUpdateSource.ListOlderAsync(maxCount, includePrereleases)`; the GitHub source lists older stable releases and, with `includePrereleases`, older `-rN` candidates too (a `-rN` tag counts as a candidate whatever flag its release carries). Same checks as before: exact platform asset, valid SHA-256 sidecar, not a draft, older than the running build. `ListStableAsync` is the same call with candidates off.
2. The view model asks for candidates only on the experimental channel (limit 8; stable keeps 3), marks candidate rows with a "candidate" label (also the installed row when the running build is a candidate) and shows an adjusted safety hint.
3. Nothing else changes in the rollback path: it reuses download, checksum, snapshot, apply, restart, the config backup and the receipt handling.

## Not covered (decided)

- Android: a package installer refuses an APK with a lower version code, so going back needs an uninstall that wipes the app data. Not offered; the Android updater stays forward-only.
- A "pin this version" switch: the updater only shows a banner for a newer version, it never installs by itself, so a rollback is not undone silently. A pin would be needed only if a silent auto-install is ever added.
- A settings-schema warning per release: the backup file is written before every downgrade; a per-release schema number needs a release-time manifest and is left for when the schema changes again.

## Verification

Contract tests (candidates and stable together newest first, a -rN tag with the stable flag still a candidate, drafts / missing checksum / newer / running build excluded, cap, stable-only unchanged, running candidate lists earlier candidates), view model tests (experimental channel, stable channel), the existing rollback tests and the rollback screenshot (unchanged baseline), run on windows-worker.

## Rollback

Revert the PR.

## Outcome

Pending.
