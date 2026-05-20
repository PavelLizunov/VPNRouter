# v2.35.0-r13 — CI fix: Linux MVM hash pin + Android NU1102 documented

## Brief

User reported CI failure email from r12. Investigation: **CI dotnet
test has been failing since r5 (Wave 39) — 9 commits ago** — and I
shipped r5..r12 without checking. Mea culpa. The release artifacts
shipped fine (Mac, Linux, Windows binaries all green) but the
dotnet-test workflow has been red since the Wave 39 ObservableProperty
addition.

## What's broken

### `MainWindowViewModelCharacterizationTests.MainWindowViewModel_PublicSurface_MatchesPinnedHash`

```
Expected (pinned): 4868da739918ff7ed09f2d117b01ca900ec3aa2ac499c9086ce6a9e68acd0279
Actual:            1d24dea2e2d97c83fc3b7a86335606288478184c7ebcfcb34958ef2d467acb18
```

Wave 39 (r5) added the `IsDnsLeakLockdownEnabled` ObservableProperty
pair (getter/setter + `OnIsDnsLeakLockdownEnabledChanged` partial).
That changes the reflection-visible MVM public surface on BOTH
Windows and Linux. The r5 commit updated `PinnedHashWindows` to
`36585b1ab04a883947dbd77028865cb061c96ca40dbe2612f3cd3ba3b7a6ee5d`
but **forgot to re-pin `PinnedHashLinux`**. Locally I always test on
Windows so the Linux miss never surfaced.

### `Build Android APK` (deferred — not blocking)

```
error NU1102: Unable to find package Microsoft.NETCore.App.Runtime.Mono.linux-x64
              with version (= 10.0.8)
              - Found 142 version(s) in nuget.org [ Nearest version: 9.0.0-preview.7.24405.7 ]
```

Per `~/.claude/projects/.../memory/MEMORY.md`: **"Wave 32b NU1102
NuGet Mono pack workaround for CI Android — deferred"**. The .NET 10
preview SDK 10.0.300 Android workload pulls a Mono runtime pack
(10.0.8) that doesn't exist on the public nuget.org feed yet —
it's on the internal `dotnet/dotnet` preview feed. CI doesn't have
that feed configured. Known issue since r4 (Wave 38a bumped to
.NET 10 SDK). Mac/Linux/Windows artifacts unaffected.

r13 does NOT fix Android. It surfaces the cost (we've been shipping
without Android CI for 9 releases) and flags it as the next
follow-up.

## Fix — r13

`VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs`:
`PinnedHashLinux` updated to the CI-actual hash
`1d24dea2e2d97c83fc3b7a86335606288478184c7ebcfcb34958ef2d467acb18`.

XML doc on the constant explains the gap: r5 missed the Linux pin
bump, r12 user-email finally caught it.

## Verification

- `dotnet build -c Release` — 0 errors
- `dotnet test` (Windows local) — passes (Windows pin already
  correct since r5)
- Linux pin will be verified on next CI run after push

## What user does

**Nothing required.** Update normally — this is a CI hygiene fix
that has no runtime effect on the binary.

## Process gap surfaced

Going forward: **check CI status after every ship**. Concrete checklist:
1. Push commit + tag
2. `gh run list --limit 5` to see what CI jobs spawned
3. Wait for failures to surface (~2 min minimum)
4. Address any failures before declaring the ship done

For the brat hotfix chain I was treating "Windows + Mac + Linux build
green" as sufficient verification but missed that the `dotnet test`
CI job (which runs unit tests on Ubuntu) was failing the entire
chain. The user's email broke my silence.

## Carry-over

Ships on top of r12 (BR-8 TUN DNS allow rule).

## Open items

1. **Android NU1102** — Wave 32b deferred since r4. Needs CI workflow
   update to add the `dotnet/dotnet` preview feed OR pin to a
   nuget.org-resolvable Android workload version. Not blocking
   user-facing releases.

2. **CI status checks in ship workflow** — should automate. A
   `gh run watch <run-id>` after each push would surface failures
   before I move on. Alternative: extend the ship-rolling-candidate
   skill to include a CI watch step.
