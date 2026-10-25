# Round U: noninteractive Linux connection lifecycle

Owner scope: eliminate repeated authentication in normal Omarchy use. Base: 2cc9e7e7.

The packaged Headless policy already refuses elevation, but generic Linux Core retains interactive connect/stop fallbacks. Missing capabilities only suggest reinstalling, without a precise repair command. Remove interactive lifecycle fallbacks, retain owned-process signaling, distinguish missing file capabilities from sandbox denial, and provide a fixed packaged-runtime repair command. Package updates remain package-manager operations; no new privilege broker or sudoers grant.

Verification: CI test.yml and arch-headless.yml at exact branch SHA; regression checks for missing-capability launch and repair diagnostics. Live checks only on omarchy-test under the shared lock. No local .NET build. UI is owned by the separate plugin worktree. Rollback: revert this task commit. Risks: legacy Linux installs without capabilities require the documented one-time repair instead of pkexec. Other platforms unchanged.

Gates: build/tests pending CI; docs in scope; independent review delegated to lead Claude; Windows UI N/A (Linux-only); mechanical characterization N/A.
