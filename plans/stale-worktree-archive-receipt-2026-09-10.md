# Stale worktree registration archive receipt

Owner approval: archive-seven-stale-worktree-registrations, explicitly approved seven metadata archives, not branch or working-directory deletion.

Executed on2026-09-10 in VPNRouter checkout. Each registration gitdir was verified to equal /tmp/<name>/.git and its parent was absent using lexists (dangling symlink would fail). Backup destinations were absent. Metadata directories moved from .git/worktrees/ to .git/cleanup-registration-backups/2026-09-10-postmerge/:

- vpnrouter-desktop-pictogram-preview
- vpnrouter-night-red-verification
- vpnrouter-ui-pictogram-catalog
- vpnrouter-vpnctl-android-deferred
- vpnrouter-vpnctl-fakeip-red
- vpnrouter-vpnctl-night-combined
- vpnrouter-vpnctl-packaging-red

SHA256 maps of every regular metadata file were computed before and after the move and asserted equal, with per-file digests in the execution tool receipt. Entire refs/heads name/OID listing asserted unchanged. No branch, commit, existing worktree directory or user file removed. Git worktree list now contains only /var/lib/dsh/Project/VPNRouter at0bcc8166510310d39e442b09a3e0e804eb94dd01 on dsh/postmerge-cleanup-20260910. Earlier separately approved VPNCTL completion backup remains in its original backup location.

Recovery: backups preserve original metadata, including index and HEAD log. Do not blindly restore stale gitdir pointers: first establish intended working-directory location and owner approval. No broad git worktree prune or cache cleanup executed.
