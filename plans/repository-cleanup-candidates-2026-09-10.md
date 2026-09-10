# Repository cleanup candidates

Status: owner approved the 44 candidates; 42 remote branches deleted after revalidation, two retained due to worktree registrations. See repository-cleanup-recovery-2026-09-10.md for full SHAs and execution receipt. Snapshot taken against main `065a2083545c85b788b302d8057be73b292f8d93` on 2026-09-10. GitHub branch tips matched the merged PR head exactly. This is evidence of accepted work, not proof of Git ancestry after squash. Re-fetch each full tip, confirm merged PR base is main and merge commit remains in main before deletion. Stop on changed tips, protection, an open PR, or an extant worktree. Keep a recovery record of full SHAs before any approved operation.

## Exact merged candidates (44)

| Remote branch | Tip prefix | Merged PR |
|---|---|---|
| codex/vpnrouter-app-config-detour | 1309ac66 | #189 |
| dsh/agent-context-migration | 30464955 | #195 |
| dsh/bound-free-config-diagnostics | 4d572907 | #209 |
| dsh/cli-run-generation | 0989d423 | #214 |
| dsh/compensate-update-restore | 90006dde | #206 |
| dsh/desktop-pictogram-preview | 93fda2c9 | #247 |
| dsh/exact-unix-singbox-stop | c7b6446f | #213 |
| dsh/feat-app-automation-telemetry | 2beb1a88 | #230 |
| dsh/filter-service-orphan-cleanup | d73c33ec | #212 |
| dsh/fix-comprehensive-hardening | 52fd80c8 | #223 |
| dsh/fix-custom-config-injector | 5f9e7f03 | #220 |
| dsh/fix-desktop-gui-races | de49497e | #222 |
| dsh/fix-etw-ruleset-and-security | 15fa2dfd | #221 |
| dsh/fix-inbox-tool-resolution-and-postrm | 7dc2a617 | #224 |
| dsh/fix-singbox-and-failover | 730069c2 | #218 |
| dsh/fix-vpnengine-lifecycle | dd685f3f | #216 |
| dsh/harden-linux-update-helper | 73be6802 | #204 |
| dsh/harden-windows-installer-trust | e7644ae3 | #205 |
| dsh/harden-windows-service-imagepath | d1af5442 | #210 |
| dsh/perf-cold-startup-and-native-commands | 12ab3cae | #226 |
| dsh/perf-idle-cpu-and-battery | 527e46db | #225 |
| dsh/perf-sockets-and-subscription-stream | d2e1a4ca | #227 |
| dsh/polish-cross-platform-ui-and-icons | 41f4db15 | #231 |
| dsh/project-prep-singbox-readiness | 89678786 | #201 |
| dsh/release-pipeline-review-20260910 | 1a97e6bd | #255 |
| dsh/remove-censored-dns | f25e4825 | #202 |
| dsh/repository-matrix-audit | 01585486 | #203 |
| dsh/split-config-generator | 8e8cc710 | #215 |
| dsh/ui-pictogram-catalog | fe17014d | #246 |
| dsh/update-shell-cli-repair | b0149a21 | #200 |
| fix/redact-prefixed-log-secrets-3111393394968998442 | d62dd6cd | #193 |
| fix-crash-reporter-scrub-ss-plugin-uris-10348075333363090544 | f16a44ea | #188 |
| fix-tgproxy-temp-file-security-4562471339280485293 | 07faa3ca | #180 |
| jules-11264095396658397065-1160ebe4 | edd3682b | #177 |
| jules-11770370228108230860-b2a55d68 | a6716cc2 | #183 |
| jules-12087695953648456052-e202b1bd | c189f09b | #181 |
| jules-15853054976605285605-df653246 | a9c17d28 | #185 |
| jules-8932622801987987748-3922a11b | c346b292 | #208 |
| jules-9773788932747885410-2459add7 | 891e154e | #186 |
| sentinel/enhance-query-param-scrubbing-16041867056846585738 | 9fff407a | #228 |
| sentinel/redact-unsupported-uri-credentials-10753305601248559367 | f7930f55 | #171 |
| sentinel/scrub-additional-proxy-schemes-8915237383111871109 | 400c040a | #229 |
| sentinel/scrub-query-param-credentials-7002285560875907611 | 840b82ca | #219 |
| sentinel-fix-argument-injection-updatechecker-12798775046056389843 | cb641495 | #198 |

## Open PR disposition proposals

- #232: merged as `9854399d` after owner-approved narrowing, independent review and green exact-head CI `34487206556` on `4b082e34`. Only span scheme filtering retained; stdlib parsing restored. Branch retained pending explicit deletion decision.
- #233: retain desktop core migration; conflicts with main and touches recently repaired release packaging. Separate reconciliation needed, not cleanup-by-merge.
- #235: retain useful subsystem docs; conflicts with main. #237 inherits this branch, so closing as redundant requires preserving docs first.
- #237: retain audit evidence; conflicts with main and inherits #235. Findings overlap #240; historical report must not re-open resolved findings accidentally.
- #240: retain approved NIGHT fixes; conflicts with main across 63 files. No blind conflict resolution or bulk merge.
- #254: merged as `1d21404a` after reviewed `23409668` passed CI `34489036398`. #253 closed after preserving data: rejection and blank-name HTTP(S) tests; both branches retained.
- #256: owner authorized merge after final review and green exact-head CI; bilingual README reviewed, integration reports being finalized.

Owner subsequently approved continuing useful integration of #233/#235/#237/#240 with reviewed conflict resolution and green CI. Substantial behavior changes still require a separate decision. This approval does not authorize discarding unique branch work.

## Retention rules

Keep main, gh-pages, current cleanup branch, all open-PR branches and every branch without verified accepted replacement. Deferred Android and verification-only FakeIP/packaging/NIGHT branches are evidence/WIP, not disposable based on age. Local branches require independent tip and worktree checks; do not infer that their contents equal the remote. Prunable worktree registrations alone do not authorize directory deletion. Preserve user untracked files and historical plans.
