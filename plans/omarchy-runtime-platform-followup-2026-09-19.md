# Runtime contract: platform impact and CI follow-up

Base: `83caab7dec4941cd92dfea5ff8e9a4234c7f21de`, draft PR #296.
Follow-up scope: CI coverage correction and source review only; no new runtime
provisioning, production trust authority, capability grants or broker.

## Confirmed coverage gap and correction

`.github/workflows/test.yml` main Ubuntu command excludes every fully qualified
name containing `Headless`. This excludes all 26 Core
`HeadlessRuntimePolicyTests`, not merely UI tests. The separate headless-contracts
job runs `VPNRouter.Headless.Tests`, a different project. Windows characterization
has an explicit filter that also omits this class.

The earlier claim of five green CI checks was accurate, but those checks did not
execute these 26 cases. They did execute in the audited Omarchy worker subset
(204 cases including 26 runtime cases, snapshot dabdc8c2).

Correction: explicit additional step in the existing Ubuntu `test` job, reusing
its Release build, exact class-qualified filter, and a distinct TRX filename.
The UI exclusions, existing Windows tests and all product sources are unchanged.
Post-push CI must show a nonzero test count of 26, not merely a successful job.
Receipt is recorded in PR #296 after completion.

## Platform impact of the preceding product commit

| Consumer | Intended behavior and evidence limits |
|---|---|
| Linux Headless | Production policy automatically enabled; unavailable until separately authorized distribution/provisioning. This is the intentionally changed client. |
| Linux desktop / CLI | No policy installation added; null-policy legacy branches remain, including existing runtime/elevation behavior. Shared source changes still create regression risk. |
| Windows desktop / CLI / Service | No default policy activation added. Existing Windows characterization/release/lifecycle-filter CI and CLI publish passed at base. Not full native UI/VPN/dataplane coverage. |
| macOS | No default policy activation added. No fresh native macOS build/runtime proof in this block. |
| Android | No default policy activation added. No Android workload build or device acceptance in this block. New native declarations in shared Core are a reason to retain platform build gates, not proof of a failure. |

`SingBoxRuntimePolicy.Current` is AsyncLocal and defaults to null. Scoped branches
were added to shared engine, startup, manager, verifier, feature and diagnostic
classes. Thus claiming that other platforms cannot be affected would be false;
intended behavior is preserved, not universal compatibility proven.

Source-verified nonblocking follow-ups are in OPEN-DEFECTS: an internal-fixture
HealthCheck warning that always says unavailable, and an outer process-name set
with ordinal comparison when unioning a policy path. Neither demonstrates a
legacy production failure: production Headless currently denies all runtime use,
and legacy candidate names plus returned PIDs already have their own deduplication.

## Next runtime decision, not implementation authority

Verdict: Extend — existing Linux pinned-archive packaging and plugin system-backend
lookup seams, with a separately approved trust/privilege design.

Existing `.github/workflows/build-linux.yml:117-136` pins sing-box-vpnctl
1.14.0-vpnctl.5 and a separate upstream Cronet archive. This is reusable build-time
input pinning, not proof of package provenance, an immutable remote release, or
runtime installation integrity. These assets were not downloaded/executed here.

Do not automatically copy the existing Debian setcap hook into an Arch/Omarchy
package: CAP_NET_ADMIN is broad network authority, not a TUN-only permission;
root-owned executable storage does not constrain caller-supplied configuration,
libraries or all network effects. Hash verification does not prove authorized
source/build provenance. A privileged helper remains a separate approval gate.

The next Micro-Spec must select the production artifact trust chain, protected
runtime/dependency layout, authenticated operation/configuration boundary,
privilege and recovery model, and host-wide routing/firewall ownership. It must
preserve the requested full native QML product, rather than quietly declaring
permanent unavailable or absent protections the finished result.

No installation, shell restart, live connection, root provisioning, release or
merge occurred. CI-only changes in this follow-up cannot change shipped runtime
behavior; they can make a previously hidden regression fail CI.
