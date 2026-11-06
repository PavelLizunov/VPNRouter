# Business 2.50.7 update-channel repair

## Intent and scope

The owner reports that an existing Business client cannot update to the newer private2.50.7 installer issued in another session. Inspect available installation evidence without asking the owner to transcribe it. Repair the shared update channel using the existing exact-source CI producer; do not rebuild or publicly expose the configured EXE.

## Confirmed evidence

- Parent checkout is clean dsh/business-pilot atb89d886f. PR652 source952e23ba9e93a28875f3eb05defd985374e97915 descends from this parent and adds only Romania presentation/tests/records and version2.50.7. Its private installer exists with that source receipt; Business update implementation/producer/workflow are unchanged.
- Trusted public latest.json still advertises2.50.6. Ingress has only shared2.50.5 and2.50.6 ZIPs. The2.50.7 private dispatch succeeded, but its shared-update job was skipped. This explains why an installed2.50.6 client does not see2.50.7; it does not prove every possible installed-client error.
- Omarchy is reachable; its guest has zero QEMU processes. SSH user sudo still requires authentication. Last accepted installed guest source was the update-enabledd2a5781f/2.50.5 bridge. Current live UI cannot be claimed from a powered-off guest.

## Plan, invariants and verification

Generate the credential-free2.50.7 ZIP in exact-source CI42184479828, validate source/sidecar/receipt/PE-resource gate, retain immutable old assets and atomically advance only latest.json. No private inputs, SDK installs, local builds, tags, merges or unrelated controller changes. Preserve provisioning/account/selection/routing and existing Caddy/DNS settings. Verify trusted HTTPS manifest and full downloaded ZIP SHA; distinguish this channel proof from live Windows apply/relaunch acceptance. Record any remaining runtime access blocker directly without another settings/version question to the owner.

## Outcome

REPAIRED shared channel. Exact-source dispatch42184479828 succeeds at952e23ba; the actual archive gate reports7passed/1expectedskip. ZIP source/sidecar/Business-win-x64-2.50.7-external receipt match;278files,73472681bytes, SHA256fb02f9d3d53c10c8551b880d082736c53958d529833ebc05c975d91fc65cd1e9. CI inspects actual PE edition/version/manifest-resource and complete archive admission. This run uses no private build inputs; no exact-private-value scan is claimed.

Only the validated credential-free ZIP was published. The manifest advanced atomically from2.50.6 to2.50.7 after a concurrent-change guard; old5/6assets remain immutable. Caddy configuration/DNS are unchanged, with no reload. Trusted HTTPS returns schema1 Business/win-x64/2.50.7 JSON, application/json and Cache-Control:no-store. A complete HTTPS download matches the validated ZIP size/SHA, using the Business user agent. This proves channel discovery/delivery, not Windows apply/relaunch.

The existing configured EXE was not rebuilt:63045466bytes, SHA2566198c9d3290d7e1bc33b6ed3d26bdd9b50eb89857468fbd6dd4dc4177965cf2d, source952e23ba. Source/sidecar/hash are verified, and the same private file is copied to Omarchy Downloads/VPNRouterBusiness-2.50.7-952e23ba with0700/0600 permissions and a second hash check. It is never published through the public feed.

A same-model linked Omarchy agent independently verifies UID/EUID1000, no effective capabilities, inaccessible Docker socket and no configured unattended VM privilege. Zero QEMU/RDP processes and no usable guest SSH banner confirm the runtime-access boundary. No prompt, privilege change, guest mutation, screenshot or build was performed there. Installed version/current UI error and actual in-app apply remain unverified; the owner is not asked to transcribe inspectable settings. No controller changes, PR652 merge, tag, release or private-installer rebuild.

Operational receipts and scripts are protected under the task delivery admin directory outside Git. The parent PR646 receives records only; PR652 continues to own the2.50.7 source.
