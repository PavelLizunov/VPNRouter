# Round U: noninteractive Linux connection lifecycle

Owner scope: eliminate repeated authentication in normal Omarchy use. Base: 2cc9e7e7.

The packaged Headless policy already refuses elevation, but generic Linux Core retains interactive connect/stop fallbacks. Missing capabilities only suggest reinstalling, without a precise repair command. Remove interactive lifecycle fallbacks, retain owned-process signaling, distinguish missing file capabilities from sandbox denial, and provide a fixed packaged-runtime repair command. Package updates remain package-manager operations; no new privilege broker or sudoers grant.

Verification: CI test.yml and arch-headless.yml at exact branch SHA; regression checks for missing-capability launch and repair diagnostics. Live checks only on omarchy-test under the shared lock. No local .NET build. UI is owned by the separate plugin worktree. Rollback: revert this task commit. Risks: legacy Linux installs without capabilities require the documented one-time repair instead of pkexec. Other platforms unchanged.

Gates: build/tests pending CI; docs in scope; independent review delegated to lead Claude; Windows UI N/A (Linux-only); mechanical characterization N/A.

## Outcome

Code SHA 5c59867394728f01fedb305a51c7bba71e44aab9: arch-headless run 40241802381 and test run 40241804270 passed. Linux test gate: 3218 passed, 77 skipped (3295 total); Windows characterization: 3386 passed, 20 skipped (3406 total); Headless contract runner: 41 passed, 0 failed. Arch smoke removes/restores file capabilities and verifies the exact repair instruction. No .NET build ran on the workstation.

On authorized omarchy-test, the exact r39.g5c59867 package was installed (replacing the previous round's r41 only for this isolated test). Removing capabilities yielded connect=false/runtime-capabilities, an exact one-time setcap instruction, connect refusal and no TUN; normal-user backend exited 0. Restoring capabilities allowed two HY2 cycles: connected in 2.15s and 1.86s, VPNRouter-TUN existed while connected and was absent after both disconnects, backend exit 0. An isolated private data directory kept the VM's subscription/settings unchanged. No interactive prompt was observed; absence of every possible polkit transaction was not traced (strace unavailable).

Source inventory: plugin setup/wrapper do not elevate; Core launch no longer falls back to pkexec; Linux stop retains direct owned-process signals and noninteractive sudo -n fallback only; all resolvectl mutations already use --no-ask-password in the accepted base; Linux firewall already uses sudo -n and is unavailable in Headless; Headless has no self-update method or UpdateChecker. Desktop privileged self-update remains separate and can authenticate. Older installed binaries need upgrading; changing source does not patch a running old daemon.

Independent integration review remains with the requesting lead Claude. This round does not merge, release or deploy to the owner's workstation.
