# Corp portal UDP and DNS association

## Intent and observed failure

Complete the owner-authorized private Corp launch. On the trusted WINBRAT,
accepted client2389fb4 passes portal TCP, signed rules and actual split/full VPN
traffic, but hostname UDP escapes through its physical vNIC after fresh owned-TUN
DNS resolution. A hypervisor capture independently matches the30-byte probe,
positive DNS request/reply and selected VLESS traffic with zero packet drops.
Record: BUSINESS-CORP-PORTAL-UDP-DNS-ASSOCIATION in OPEN-DEFECTS.md. Raw captures,
private configuration, credentials and installer remain outside Git.

## Frozen correction contract

- Generated Business DNS must retain IP-to-domain associations from its replies,
  so IP-addressed UDP can reach the preceding portal domain rejection rule.
  Emit `dns.reverse_mapping: true` in both split and full Business configurations.
- Add nullable `SingBoxDns.ReverseMapping` with JSON name `reverse_mapping` and
  omission when null. Unconfigured/default and Standard generation keep that
  field absent. Do not change the Standard default wire shape.
- The Business portal setup establishes this flag even without an accepted
  optional remote policy; the protected portal is an edition invariant.
- Keep exact selected direct VLESS TCP, dedicated HTTPS DNS through that same
  outbound, bootstrap resolver, UDP rejection precedence and process/telephony
  protections. Preserve pre-TUN LAN/user exclusions. Do not hardcode resolved
  portal addresses, change DNS ownership or introduce a global UDP block.
- This correction does not claim protection for arbitrary DNS bypass, excluded
  destinations or an unrelated hostname sharing an address. The owning live
  gate exercises fresh portal resolution through the captured TUN DNS.

## Interfaces for independent tests

`ConfigGenerator.Generate(Profile, IEnumerable<string>, AppSettings,
 bool? strictDnsOverride = null, Func<VlessServerEntry,bool>? isServerAlive = null)`
returns `SingBoxConfig`; `ConfigGenerator.Serialize(SingBoxConfig)` emits the
actual source-generated wire JSON. `ConfigGenerator.ApplyBusinessPortal` is
internal and visible to VPNRouter.Tests. `ProductEdition.IsBusiness` is a compile
constant; existing CI executes both Standard and Business test assemblies.
An independent writer may use existing test fixtures, not implementation files.
The established `domain_resolver` wire union permits a string tag or an object
with `server`/`strategy`; require the semantic bootstrap target `local-dns`.

Required regressions: Business split/full serialization has explicit true;
default nullable DNS omits the field; Standard generation keeps it absent;
manual portal helper activation in Standard-test assemblies exercises the same
Business invariant. Retain exact selected portal outbound/DNS and preceding
UDP guard assertions. Do not model runtime success by merely matching a supplied
hostname; the actual owned binary capture below is the runtime oracle.

## Verification and rollback

No development-workstation or harness build, package or full tests. Source
checks and independent diff review precede publication; exact-head CI gates
acceptance. Then build the private full installer on leased WINBRAT from the
accepted SHA with the preserved complete bundle. Keep prior installer and all
credential versions. No public release/tag and no SDK installation.

Repeat the exact hostname UDP probe with a physical positive DNS control and
selected transport on the hypervisor tap in split and full modes. Require control
packets present, zero capture drops, portal HTTPS from verified exit, zero portal
plaintext DNS and zero portal UDP through the physical vNIC. Finish empty,
stopped/apply-now and restored32-rule policy acceptance; leave VPN stopped.
Before any CI yield restore the intended rule list at a higher signed revision.
Rollback retains existing provisioning/journal/floors and republishes prior
content at a newer revision; never reset the policy revision or credential set.

## Publication receipt

Separate implementation and spec-only writers completed disjoint files. The
complete diff-only review returned no important or critical findings. Root
corrected a test's overly narrow resolver representation assumption while
retaining the required local-dns target, and recorded it before correction.
Whitespace and gitleaks checks passed; no local .NET build/tests were run.

Before publication, the original32-rule list was restored at signed revision4.
WINBRAT independently verified its P-256 signature, exact rules and applied
core configuration after explicit reconnect, then stopped: core0/TUN0. The
old2389fb4 installer is retained and marked superseded, not ship-ready.
The worker lease remains held for the corrected exact-SHA private build.
Fresh CI, both-mode physical capture, empty/stopped tests and final restoration
remain required; the source patch alone is not evidence of runtime correction.


## Accepted private build and upgrade

PR #677 was accepted into business as 4422aea7c1c977bef7c2d79411dc031e57e99023;
its tree matches tested head a4a770c6. All 13 applicable CI jobs passed;
three manual delivery jobs were skipped. No workstation/harness build was run.
The private 2.50.8 installer was built on leased, preflighted WINBRAT:
63,092,706 bytes, SHA256
60dbe8b12b88513062627b90b686d1c7b0a0d76afa12e8cf8ee6dc7efa100a9e.
Installer and complete private package were permanently archived and verified.

Actual upgrade verifies all 278 payload hashes, DLL source 4422aea, complete
embedded/persisted provisioning and all eight protected state files.
Journal floor 4 and Standard baseline are retained. An independent administrator
grant revocation changed the live subscription during acceptance. Fresh canonical
subscription and read-only inventory agree on all five current choices; the
client follows that source. Prior identities remain archived. The task-only
preservation oracle now distinguishes durable bytes, current subscription content
and expected lifecycle-file updates. Product and grants were not changed.

Both actual split/full physical captures pass: positive DNS request/reply,
151/134 selected VLESS TCP packets, zero physical portal DNS/UDP443 and zero
capture drops. Fresh owned-TUN DNS and portal HTTPS 200 pass in both modes.
UDP receive times out (10060); explicit socket rejection is not demonstrated.
Raw captures and complete Windows evidence remain in permanent private archives.
Final acceptance passed on backend 0.9.0+23beb92: empty apply-now without
consent returns422; consented publication creates revision5 while the main VPN
stays stopped. Client accepts it as pending without auto-start. App restart
retains the identical signed journal; explicit split/full connections apply zero
editable sites in actual route and DNS. Protected portal/telephony remain;
selected and ordinary process IPv4 HTTPS follows the expected mode defaults.
A task-only full-mode assertion was corrected against the existing final=proxy
contract; no product changes or redundant traffic reruns were required.

Original32 rules/comments were restored as revision6 (apply=now). Independent
OpenSSL P-256 verification confirms the exact journal payload, policy identity,
floor/digest, all32 route/DNS matches, applied core acknowledgement and no trial
domains. Actual semantic full config matches the original revision4 baseline.
Final WINBRAT state: app0/core0/TUN0/publisher0, port18443 closed, floor6,
complete provisioning unchanged; split preference and healthy endpoint retained.
Full server state, private signers, reader credentials, password and complete
private bundle are permanently retained and verified. Temporary capture files,
probe/publisher binaries and ingress test logging were cleaned after archival.

Six gates: build PASS (exact-source CI and private worker build); tests PASS
(Standard and Business CI plus actual scoped acceptance); documentation PASS
(this receipt and ledgers); independent review PASS (separate spec writer and
diff-only reviewer, no findings); Windows runtime PASS (source/hash, both modes,
policy lifecycle and final cleanup); integration PASS (real portal/VLESS egress,
independent signature and generated route/DNS evidence).

Four of the current five subscription exits are verified. The earlier sixth
choice was independently revoked by an administrator during acceptance; its
prior state is archived. The remaining unverified direct path, general capture
exclusions and hypervisor capacity remain documented limitations. Node-health
indicator slices A/B/G remain separate from completed portal delivery C/D/E/F.
No public tag/release or shared-channel publication was made.
