# YAML preview helper verification — 2026-09-07

## Scope and root cause

Reviewed task-owned `Program.cs` working-tree fix over base a3968c4fbd15752a41fc554811c957903287b0a9, including inherited diagnostic edits. Final Program.cs SHA256: fb6f42cbf5464b228a356533b3a4e2bba51e4971d9e4e4e7c30493426616c98f.

Read-only actual-config diagnostics first returned `ParseEvents/Anchor`; complete event scan then returned `ParseEvents/Alias` with tag rejection checked first. Thus legitimate YAML anchors and aliases, not explicit tags, caused the blanket pre-parser rejection. Actual content, identifiers, and config hashes were never emitted. Serializer origin is not proven.

Fix accepts unrelated references, rejects references to app mapping or edited scalars, and rejects cyclic graphs using reference-identity active/visited sets. Existing explicit-tag, merge-key, duplicate-key, document-count, boolean-style and exact scalar-source-span restrictions remain. Transformation still copies original UTF-8 bytes outside selected scalar spans rather than serializing the graph. Missing target keys retain existing default-false behavior.

## Executed evidence

WINBRAT identity checked before remote mutation; task SDK 10.0.302. Preflight available memory 9,973,196 KiB, CPU 33%, disk free 113,739,395,072 bytes. Only task helper source/scripts copied to task checkout; no SDK provisioning or process changes.

- Existing diagnostic baseline binary plus synthetic unrelated mapping alias fixture: `--inspect` exit 1, `stage=ParseEvents reason=Alias` (red).
- `C:\dotnet\dotnet.exe run --project plans\preview-settings-helper -- --self-test`: exit 0, `self_test=true` (green).
- Fixed binary synthetic fixture `--inspect`: exit 0, `inspect=true validation=true`.
- Fixed binary actual config `--inspect`: exit 0, same closed-enum result.
- `git diff --check`: exit 0.
- Protected backup ACL inheritance disabled; output absent before creation.
- `powershell -NoProfile -ExecutionPolicy Bypass -File C:\Temp\vpnrouter-pictograms-20260907\plans\preview-settings-helper\verify-preview.ps1`: exit 0. Output: `success=true autostart_vpn=false autostart_zapret=false autostart_tgproxy=false`; `inspect=true validation=true`; `source_unchanged=true protected_preview_exists=true preview_valid=true`.

Created only protected `C:\ProgramData\VPNRouter-pictograms-20260907-backup-392ffe3d184440f7a4558dfb28cad913\config.preview.yaml`. Source hash compared before/after in memory, not printed. No actual config overwrite, app launch, install, permanent policy/trust change, commit or push.

## Security/change review

Skills used: homelab, ponytail, security-review, change-verification. Source-to-sink: CLI read -> UTF-8/parser -> validated mapping/scalars -> exact source spans -> reparse -> CreateNew-only output. Closed enums prevent YAML exception leakage. Cyclic aliases, unresolved aliases, aliased app mapping and booleans, duplicate keys, merge keys and tags have rejection regression coverage; unrelated nested mapping/sequence and scalar aliases have byte-equality acceptance coverage. History identifies original helper in 32ccbd19. Parent reports independent reviewer PASS for exact helper hash above.

Verdict: verified within helper/output-only scope. Full app install/runtime and VPN dataplane are outside this verification. No universal YAML/security proof is claimed. Existing input-size/resource ceilings were not redesigned; output caller remains responsible for protected directory and machine identity. A first synthetic fixture attempt used a relative .NET file path despite PowerShell Set-Location and failed ReadInput/IoFailure; retried with exact absolute synthetic path, not secret data.
