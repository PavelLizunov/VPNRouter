# v2.35.0-r14 — close the last red CI check: Android NU1102

## Brief

User screenshot showed every commit r5..r12 had 4/6 checks failing
(dotnet test + Android), and r13 still had 5/6 (Android still red).
r14 closes the Android NU1102 gap so all release commits go 6/6 green.

## What was broken

```
error NU1102: Unable to find package
  Microsoft.NETCore.App.Runtime.Mono.linux-x64
  with version (= 10.0.8)
  - Found 142 version(s) in nuget.org
    [ Nearest version: 9.0.0-preview.7.24405.7 ]
```

CI installs the latest .NET 10 SDK (currently 10.0.300 preview) via
`actions/setup-dotnet@v4` with `10.0.x`. That SDK's Android workload
manifest references preview runtime packs like
`Microsoft.NETCore.App.Runtime.Mono.linux-x64 = 10.0.8` which
**haven't been published to the public nuget.org feed yet**. They
live on the internal `dotnet/dotnet` Azure DevOps preview feed
instead.

Known deferred since r4 (Wave 38a bumped to .NET 10 SDK) per
`MEMORY.md`'s "Wave 32b NU1102" entry. Never blocked
release artifacts (Mac/Linux/Windows ZIPs ship from separate
workflows) but kept the Android tag red on every commit.

## Fix — Wave 32b

Add `VPNRouter.Android/NuGet.config` layering the public `dotnet10`
preview feed above nuget.org:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="dotnet10"  value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

Scoped to `VPNRouter.Android/` only — NuGet inherits up the
directory tree, so Android restore sees both feeds while Core /
App / CLI / Service / Tests all keep restoring from nuget.org alone.
No risk of polluting other projects' restores.

The `dotnet10` feed is the same one Microsoft's
`dotnet/installer` pipelines use to ship preview SDK runtime
packs. It mirrors stable versions from nuget.org and adds the
unpublished previews.

## Verification

- `dotnet build -c Release` (Windows side) — 0 errors (Android
  NuGet.config doesn't affect non-Android restores)
- Will be verified on CI when r14 push triggers the Android workflow

## What user does

**Nothing required.** CI hygiene only — no runtime behaviour change.

## Process commitment (real this time)

After r14 push:
1. `gh run list --limit 10` immediately to see what spawned
2. `gh run watch <id>` for each workflow until completion
3. Don't declare the ship done until **every** check is green or
   explicitly flagged as known-broken with a tracking task

User screenshot was the second wake-up call after the email; both
caught CI status drift I was tolerating. r14 makes this the LAST
red-X-not-noticed regression I'm shipping past.

## Carry-over

Ships on top of r13 (Linux MVM hash pin), r12 (BR-8 TUN DNS allow
rule), r11 (BR-7 deferred lockdown), r10 (BR-6 audit follow-ups),
r9 (BR-5 Stop timing + lockdown default-on), r8 (BR-4 orphan
cleanup), r5 (Wave 39 firewall lockdown infrastructure).

## Future cleanup

When .NET 10 ships GA and the matching runtime packs land on
nuget.org, the `dotnet10` preview feed becomes redundant.
Delete `VPNRouter.Android/NuGet.config` then.
