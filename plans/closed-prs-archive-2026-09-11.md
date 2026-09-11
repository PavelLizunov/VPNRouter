# Closed PR archive and recovery

Owner explicitly selected archive and delete remote links for #178, #196, #197, #207, #217, #234, #236, #241, #242, #245 and #248. This preserves unfinished work, not acceptance or rejection of its behavior. New #258/#259, Android WIP, performance WIP, main and gh-pages are excluded.

Archive: `/var/lib/dsh/Project/VPNRouter-cleanup-archives/2026-09-11-closed-prs/closed-prs.bundle`

- Size: 225738850 bytes.
- SHA256: `98e47605064a86286fea37dfc986c771a2d1e4a0b5e4d9377b24b031a6144dcf`.
- Independent restoration: sibling `restore-check.git`, initialized empty; no source-repository alternates used.
- `git bundle verify` in empty bare repository confirmed complete history, no prerequisites.
- All eleven refs fetched from bundle and individually matched original tips; `git fsck --full` exit0. Unborn main HEAD notice is expected in this archival bare repo; restored branches exist.
- Archive and bare restore retained locally, not off-host backups.

Original tips (PR order above):

```
178 94e89ad0523359838a969e53ec7094e9a1cec486
196 48f4bd83f65e4178ea98cbc4d5b8416980407481
197 2a816d20c59112021a87eb49cba67451f5873755
207 9193069593c43759f812be15e3669aea99f49d88
217 a5d6cdf51557474c540165659fc48c18e4aee2d4
234 8251eb30932d00b7baea6c9e3f27da677ee41c02
236 7a40c402cd152f59dc7278873ef034a932e01d82
241 21b16565c0142de34225e327d999571487c22c52
242 053b31b7d9d511274279269e292553dce8a9d882
245 e9b84314f9499907c5e0c710c4aeb0c37c8ebbd4
248 000f7650ad3fd595cbbeaef7f179c5444a06268a
```

Recovery: list exact archived names with `git bundle list-heads <bundle>`. In a new bare repository run `git fetch <bundle> <archived-ref>:refs/heads/recovered-pr-N`, then compare `git rev-parse refs/heads/recovered-pr-N` against this table. Verify SHA256 before recovery. Do not automatically push or execute recovered work.

Status: archive verified and all eleven remote refs deleted on 2026-09-11 UTC. Immediately before deletion SHA256, each remote tip, each independently restored tip and worktree exclusions were rechecked. One atomic push used eleven explicit exact-SHA leases. Fresh remote listing confirmed all eleven absent and every excluded remote tip unchanged. Six remote branches remain: main, gh-pages, Android WIP, current cleanup branch and new PR258/259. No local branches or product files changed. Earlier preservation notes remain historical analyses; outstanding test and policy differences are not resolved by archival.
