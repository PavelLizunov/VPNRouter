# N4 merged-head acceptance

Scope: merged Headless and Omarchy plugin on the authorized test VM only.
Keep the desktop escalation fallback, pinned runtime and noninteractive
resolver changes. Repair the observed MTU/restart resolver race in the Linux
adapter and cover delayed TUN appearance plus physical-link rejection.
Use dispatch-only Arch/test CI; no local .NET builds. Report exact revisions,
consumer results and remaining limits in the private M.md acceptance record.

Observed before fix: MTU is 1280, full-tunnel route uses table 2350, but the
new link has no DNS scope and uncached www.gnu.org resolves through ens18.
The adapter error identifies ens18; this is a startup race, not a missing
VPNRouter-TUN polkit permission.
