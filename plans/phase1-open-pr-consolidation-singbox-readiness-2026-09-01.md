# Phase 1: open-PR consolidation and sing-box readiness

Date: 2026-09-01
Branch: `dsh/project-prep-singbox-readiness`
Accepted base: `origin/main` at `a393e028886a70addaf376262a576163f447de14`

## Why

The directly mergeable PRs and native DSH migration are now on `main`. Several remaining bot PRs contain one useful hardening fragment mixed with stale, duplicate, unsafe, or provider-specific changes. VPNRouter also needs a fail-closed answer to ISP/RKN spoofing of public UDP DNS and a source-verified plan for the requested stable sing-box 1.14 LX migration.

Consolidate only the surviving changes once, update the active documentation, and close superseded PRs with an exact replacement reference.

## What

- Prove `tg_proxy_secret` coverage on top of the merged prefixed-secret redactor from #193.
- Port #179's safe `getcap` `ArgumentList` call without its `global.json` downgrade.
- Combine the unique CrashReporter protections from #184, #187, and #190 with merged #185/#188 behavior.
- Replace VPNRouter-owned direct public UDP DNS in custom bootstrap, geo-bypass, and deep-verifier configs with Cloudflare/Yandex DoH while preserving proxy DNS, split routing, LAN/system exceptions, and bootstrap loop avoidance.
- Port app-config detour guidance from removed Claude files into active scoped `AGENTS.md` files and extend the provider-artifact contract to `.jules`.
- Correct action-version comments and `tools/native-deps.md`; update bilingual public DNS documentation.
- Add a durable sing-box LX 1.14/AWG 3.1/Android readiness plan based on current upstream evidence.
- After this PR merges, close #172, #176, #178, #179, #182, #184, #187, #190, #191, #194, #197, and #199 with their duplicate, blocked, rejected, or replacement rationale.

## Non-goals

- No sing-box or Android AAR binary bump in this phase.
- No claim that current LX supports AmneziaWG 3.1.
- No blanket rewrite of user-supplied custom DNS servers.
- No tag, release, deployment, stable cut, or installation.

## How

Reuse existing regex and typed DNS models. Add only the missing alternatives or tests, use `ProcessStartInfo.ArgumentList`, and synthesize the same HTTPS DNS shape already emitted by `ConfigGenerator`. Document external gates instead of implementing speculative LX/AWG3 compatibility.

## Risk and rollback

Risk is medium: redaction and DNS defaults are security-sensitive, while incorrect native dependency documentation can lead to an unsafe future build. Revert the implementation commit to restore prior behavior; a failed check or unresolved review finding blocks merge.

## Verification gates

1. **Scope gate:** only reviewed salvage, DNS bootstrap, active DSH context, tests, and task-owned documentation change; `.claude`, `.agents`, `CLAUDE*.md`, and `.jules` remain absent.
2. **Secret gate:** CrashReporter and Diagnostics tests cover basic-auth, pathless query/fragment, Shadowsocks plugins, `tg://`, prefixed secrets, and `tg_proxy_secret` without exposing values.
3. **DNS gate:** VPNRouter-owned generated, injected, and deep-verifier DNS contains no direct public UDP/53 resolver; proxy DNS remains tunnel-detoured and LAN/system or explicit custom resolver exceptions remain explicit.
4. **Build gate:** focused tests, full discovered suite, `grep`, Windows Go tests, characterization contracts, and applicable updater packaging checks are green on the exact reviewed head.
5. **Documentation gate:** English/Russian README behavior matches code; native pins/tags/checksums are source-backed; LX `.29` is rejected until final `v1.14.0` ancestry is proved and Android Clash API compatibility is gated.
6. **Review gate:** independent correctness, security, DNS/leak, compatibility, and documentation reviewers leave no surviving P0/P1; no release action is performed.

## Outcome

Implemented in PR #201 at code head `801f1d04f8f86041b8d5ef226416e872f5a8a85a`. The consolidation preserves merged redaction behavior while adding HTTP userinfo, pathless query/fragment, and `tg://` coverage; pins `tg_proxy_secret`; ports the `getcap` argv boundary; and removes VPNRouter-owned direct public UDP DNS from generated, custom-injected, geo-bypass, and transient deep-verifier configs. The obsolete RU-bypass/DNS-lockdown warning is retired without removing its public compatibility surfaces.

Active DSH guidance now owns the app-config detour contract and rejects `.jules`; bilingual public DNS behavior and the native runtime inventory match source. The separate readiness plan rejects beta-based LX `.29`, keeps AWG 3.1 and Android Clash API compatibility externally gated, and makes no runtime pin change.

Local `git diff --check`, workflow YAML parsing, adversarial redaction cases, source/pin checks, and provider-artifact scans passed. Five initial and three final independent reviewers found no surviving P0/P1. GitHub Actions on the implementation head passed `test` (2,826 total: 2,769 passed, 57 platform/UI skips), `characterization-windows` (19/19), `go-test-windows`, and `grep`; updater packaging was not applicable to this path set. The control plane has no .NET SDK or PowerShell, so GitHub Actions is the build/test oracle. This outcome-only commit must pass the same exact-head checks before merge.

Rollback is a revert of `801f1d04f8f86041b8d5ef226416e872f5a8a85a`. No binary bump, release, tag, deployment, stable cut, or installation was performed.
