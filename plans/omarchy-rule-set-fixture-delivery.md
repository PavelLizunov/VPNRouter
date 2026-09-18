# Standalone rule-set fixture isolation

Scope: VPNRouter.Tests/ConfigGeneratorRemoteRuleSetGuardTests.cs only; no product
runtime changes. Base: b8bde92c99448c6321396fcaf14f9a9f6f1eae93 (PR296).

## Requirement and implementation

The original fixture allocated an unused private directory while generation used
testhost-wide AppPaths/cache. TestEnvironmentSafety already isolates the testhost
from live data. Confirmed defects are shared-cache/network dependence and
vacuous acceptance when expected local entries are omitted.

The fixture now redirects/restores AppPaths, seeds fresh rule-set cache leaves
and minimum-size geo files, checks prerequisites before generation, and requires
exact local tags, contained paths, binary format declarations and fixture bytes.
The four toggle combinations and custom geosite/geoip case cannot pass merely
because the generator returned no entries. No changed production files needed.

## Verification

Earlier red9957facaa36ed4755151a00c941c50b984998c16 ran only the new fixture fact:
it failed at the private-AppPaths assertion before generation. Integrated green
6c7fc82cd427773ad627c1cae264c9ab5a3a9103 passed all6 cases.

For standalone delivery, snapshot b1ef00072ae109c2d6495df7955ae7ff01cf206c contains
published base plus only the modified fixture. Exact-tree isolated worker check:

```
dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --nologo -v quiet -clp:ErrorsOnly
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --no-restore --nologo --filter 'FullyQualifiedName~VPNRouter.Tests.ConfigGeneratorRemoteRuleSetGuardTests.'
```

Result: bash-47 exit0; Release build425 warnings/0 errors; 6 passed,
0 failed, 0 skipped (306ms). Worker uses pinned SDK10.0.301 and
private HOME/XDG config/cache; development-certificate generation disabled.
Coordinator directly reviewed the narrow diff, constructor cleanup, original
path restoration and expected tag/cache mapping. Independent full-system review
is outside this test-only block. git diff --check passed.

## Limits

Seeded bytes are deliberately NOT valid SRS. Tests verify generator shape/cache
hits, not sing-box compatibility, network denial or packet-level behavior.
No live VPN, installation, privileged operations or release. Full Core and
cross-platform CI results must be evaluated separately after push. Broader
Omarchy implementation and SplitCharacterization isolation remain outstanding.
