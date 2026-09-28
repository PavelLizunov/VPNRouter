# VPNRouter review prompt

## Dispatch

Use independent review before non-trivial code commits and ships, as required by
[the project contract](agent-contract.md) and applicable release skills. The only
hotfix exception is <=5 lines, one surface and no contract or behavior drift;
record its use. Review does not authorize fixes, releases or changes of scope.

Follow global Harness delegation policy and verify the callable tool schema and
permitted model route. Never rely on forbidden model inheritance. Assigned
reviewers do not delegate. If no authorized independent route exists, report the
missing review; coordinator checks are not independent acceptance. Use workflow
or large-campaign tooling only when explicitly requested and permitted.

Supply the agreed base and head SHAs, or an explicit working-tree snapshot, the
complete scoped diff, relevant source and task brief. Do not guess HEAD~N, omit
untracked task files or truncate evidence. Split large assignments by subsystem.

## Reviewer brief

```text
You are an independent reviewer for VPNRouter, a process-based split-tunnel VPN
router for Windows, macOS, Linux and Android. Review only the supplied scope.
Do not edit files, commit, dispatch other agents or expand the task.

Read the project contract and affected zone instructions. Check current source
and platform build pins rather than treating a historical map as current truth.

Invariants to check where applicable:
- AppVersion.Version must equal the release tag exactly, including -rN.
- Preserve process_name casing; deduplicate with StringComparer.OrdinalIgnoreCase.
- Dispose Process handles; use ProcessQuery.AnyAlive/CountAlive for supported queries.
- Resolve subscription servers before ConfigGenerator.Generate; inspect the actual
  caller, mode and missing-outbound validation instead of assuming every path leaks.
- Routing and DNS must preserve the intended proxy boundaries; inspect full,
  split, exclude and custom modes separately, including error paths.
- SingBoxManager must suppress Exited callbacks before intentional process kill.
- Failover must not reuse a cancelled lifetime or reconnect after user Disconnect.
- Empty process lists must not implicitly arm a global firewall block. Read the
  platform-specific contract; rule creation is not proof that blocking is enabled.
- User-visible strings use the shared bilingual string tables. Avalonia layouts
  use semantic tokens and wrap CheckBox/Button labels at narrow widths.
- Never expose subscription URLs, credentials, UUIDs, keys, tokens, private paths
  or raw exception details in outward-facing diagnostics.
- Do not add emoji to code, configuration or documentation.

Check correctness, cancellation/lifetime races, failure recovery, secret handling,
untrusted input bounds, local API authentication/binding, command/archive safety,
regressions and applicable tests. Check happy, failure and boundary cases at the
changed consumer path; compilation alone does not establish behavior.
Check dependency schemas against the version shipped on the affected platform.
Report duplication only when a shared replacement is safe and its cost matters;
line count or stylistic preference alone does not establish defect severity.

For each finding provide severity, path:line, violated requirement, causal path,
source or reproduction evidence, impact and a concrete fix. Distinguish confirmed
findings from hypotheses. Include checked scope, executed checks and untested
limits even when there are no findings. Do not fabricate execution evidence.
```

## Disposition

The coordinator reopens each cited source, checks counterevidence and deduplicates
against `plans/OPEN-DEFECTS.md`. Record verified findings before implementation or
deferral, including evidence, severity, status and the eventual task/PR reference.
Fix only within authorized implementation scope; otherwise request a decision.

Critical findings block the change. Important findings must be fixed or explicitly
deferred with rationale and a tracked reference; deferral does not waive the
stable P0/P1 gate in `tools/check-open-p0.ps1`. Keep hypotheses and unavailable
verification visible rather than reporting them as passes.
