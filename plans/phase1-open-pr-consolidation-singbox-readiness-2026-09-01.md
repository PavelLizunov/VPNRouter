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
- Replace VPNRouter's synthesized custom-config UDP bootstrap at `1.1.1.1:53` with direct Cloudflare DoH while preserving proxy DNS, split routing, LAN/system exceptions, and bootstrap loop avoidance.
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
3. **DNS gate:** generated defaults and synthesized custom bootstrap contain no direct public UDP/53 resolver; proxy DNS remains tunnel-detoured and LAN/system resolver exceptions remain explicit.
4. **Build gate:** focused tests, full discovered suite, `grep`, Windows Go tests, characterization contracts, and applicable updater packaging checks are green on the exact reviewed head.
5. **Documentation gate:** English/Russian README behavior matches code; native pins/tags/checksums are source-backed; LX `.29` is rejected until final `v1.14.0` ancestry is proved and Android Clash API compatibility is gated.
6. **Review gate:** independent correctness, security, DNS/leak, compatibility, and documentation reviewers leave no surviving P0/P1; no release action is performed.

## Outcome

Pending implementation and exact-head CI.
