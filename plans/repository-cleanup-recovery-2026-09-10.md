# Approved branch cleanup recovery record

Owner approved the 44 candidates in repository-cleanup-candidates-2026-09-10.md. Preflight verified exact branch tip = merged PR head, base main, accepted merge commit ancestor of main 065a2083545c85b788b302d8057be73b292f8d93, no protection/open PR. Registered worktrees exclude #246 and #247; their branches are retained. No directory cleanup authorized.

The branch names are recorded in the candidate manifest, keyed by PR number. Full source tips for the 42 eligible branches follow. Deletion must compare the full old tip atomically (Git lease); changed tips are not authorized. These are recovery pointers, not claims that a local bundle exists. GitHub pull refs retain the original PR head; recovery can fetch refs/pull/NUMBER/head and verify the expected SHA before recreating the original branch with owner approval. Local matching copies may be removed only without registered worktrees; divergent copies are retained.

| PR | Full original head |
|---|---|
| 189 | 1309ac66df682c7715ae55e554f74480b2c6749d |
| 195 | 304649557af69192fc17e9714d0549d621c9085c |
| 209 | 4d572907d39363b73dee920276a57fe58f95b6d9 |
| 214 | 0989d4231568766f2b9d275cba1930f1eb7d3ff3 |
| 206 | 90006dde039436e1bb1e0ecce357ce03adddc490 |
| 213 | c7b6446ffc00dbdd81ecf91423a3fb74e23940f2 |
| 230 | 2beb1a880596e9cda357d855026cbacbc976d2a9 |
| 212 | d73c33ec4044f5105292b4b7d42ebd41c436d63e |
| 223 | 52fd80c8a2fc44487de2b5ed429280096a6699b6 |
| 220 | 5f9e7f0358f01581cc4d68a4bd723bd1c19ecf8c |
| 222 | de49497eb587825cef2093e74cb22a7c09d82a78 |
| 221 | 15fa2dfd6fc55bae1ac206061a214073171e2ed7 |
| 224 | 7dc2a617ca695f62f4abbe5668a472fc76a00f54 |
| 218 | 730069c27f3e870c67c01cd24997758e1ec8a3d9 |
| 216 | dd685f3fdba0fc114c46976492ad6cf83addbe5c |
| 204 | 73be6802cd6efb1c451080088760b47cb3738fb6 |
| 205 | e7644ae310d14bce55632c78be360ef1e4afdb79 |
| 210 | d1af5442d5f5c8b7d48f0a9c0502a43ac5d8e950 |
| 226 | 12ab3cae2d13ada243bf7a28658f0ef2cd48cdc4 |
| 225 | 527e46db42ca59b3efa55c8551ef3046d974a371 |
| 227 | d2e1a4caa1b82c22aab1e9463cdd59cbadb79c6e |
| 231 | 41f4db15f8f6ed57a5a82bda7dbbf80110e0ec91 |
| 201 | 89678786f0b64c970c9dab99fabf54e033f95906 |
| 255 | 1a97e6bd6f6accca3e8f6942a15ab9d78bcbaacd |
| 202 | f25e4825e9e3a7e9d844dbc9c80da0d104255547 |
| 203 | 015854867de06d66bd741c57eeda2378c1e58881 |
| 215 | 8e8cc710365b46016098bd4dcaa70d72bf9ee6b1 |
| 200 | b0149a21fc40f001d0662697efead96f9c05a4da |
| 193 | d62dd6cdb44e90f9319e5ff7acaa09adb313999b |
| 188 | f16a44ead92bc78f07e5e4c6550d4b5bbd8bfbab |
| 180 | 07faa3ca1f22c617afcdcf073735e3eb3f606215 |
| 177 | edd3682bd9085c418a8c70b6859a1037ef664191 |
| 183 | a6716cc2ef47e7510f4ae999ac0f30c5692e8423 |
| 181 | c189f09b65162d6af0a05be8596fb88c2d041867 |
| 185 | a9c17d28de8a0cfb5b2554cdd4212454d7de2786 |
| 208 | c346b2925b7a82b08388690410b75cbf14c26f1c |
| 186 | 891e154ea3d10fbd9a14dd3a824e678a224149ac |
| 228 | 9fff407a4284c7d25b94d94f6aeac83c2e337cde |
| 171 | f7930f5519e8558b625d707e29ee85ab59f136f7 |
| 229 | 400c040adcc27823b5f9f0c3b49a1ae525947685 |
| 219 | 840b82ca1fde8667c6b8ad965935278ebc82986f |
| 198 | cb641495c05b40bb3d9032369449a5609073032d |

## Execution receipt

Completed approved first batch: git push --atomic with 42 explicit old-SHA leases deleted all 42 eligible remote branches (exit 0). Fresh GitHub branch listing confirmed their absence, main remained 065a2083545c85b788b302d8057be73b292f8d93 and gh-pages remained present. Deleted 13 local same-name copies after exact full-SHA and registered-worktree checks (exit 0). Deleted local copies: codex/vpnrouter-app-config-detour; dsh/agent-context-migration; dsh/bound-free-config-diagnostics; dsh/compensate-update-restore; dsh/filter-service-orphan-cleanup; dsh/harden-linux-update-helper; dsh/harden-windows-installer-trust; dsh/harden-windows-service-imagepath; dsh/project-prep-singbox-readiness; dsh/release-pipeline-review-20260910; dsh/remove-censored-dns; dsh/repository-matrix-audit; dsh/update-shell-cli-repair. Other local branches retained. The desktop-pictogram-preview and ui-pictogram-catalog branches remain because of worktree registrations; no worktree metadata or directories were deleted. No open PR closed or merged by this batch.
