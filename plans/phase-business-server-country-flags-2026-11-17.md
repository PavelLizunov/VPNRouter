# Business server country flags

## Intent and scope

Owner reports two new Business servers have no flag, recurring for the third
or fourth time. Provide flags from IP-derived country metadata without server
name conventions. Approximate country database results are acceptable; precise
city/region accuracy is not required. Keep the active Corp company-isolation
work and delivery objective.

## Confirmed source findings

Delivered private client2.50.10 source57a27c1 restricts BusinessServerCountry
to10 countries, including a test requiring CA to be blank. The view model
accepts nullable metadata without an IP fallback. Current vpnctl metadata
lookup returns no country on any missing address lookup or conflicting result.
GeoLite City must be loaded; updating a file alone does not reload it.
Sanitized live metadata inspection confirms that the two reported nodes return
IT and AL, respectively; neither code is supported by the current renderer.
GeoLite City and ASN databases are configured and present. This identifies the
client renderer as the immediate cause for those two nodes.

## Invariants and verification

Preserve subscription identities, protocol endpoints, credentials and routing.
Find and document the actual two-node metadata/lookup/rendering failure without
exposing IPs or tokens. Use comprehensive valid ISO country support, with no
country-by-country whitelist. Define deterministic approximate lookup behavior
for mixed known/missing IPs and exercise newly added DNS/IP nodes.
Verify supported and previously unsupported countries, absent metadata, IPv6,
health-sample independence and country database recovery.

## Client preparation

Prepare bundled flag images for ISO country codes with safe handling of unknown
codes and focused source regressions. Metadata and routing remain unchanged.
The owner explicitly authorized necessary builds and release on2026-11-17.
Deliver Corp2.50.11 from the accepted Business branch40a55aa2 (the same source
tree as delivered57a27c1), preserving the complete private bundle and shared
credential-free update contract. Public main and public multi-platform releases
are outside this private Windows delivery. Follow the existing Business delivery
runbook and release/verification skills where applicable. Server lookup changes
remain a separate follow-up.

## Execution status

Prepared a bundled 250-flag set (249 ISO 3166-1 alpha-2 codes and XK),
with attributed flag-icons v7.5.0 source and MIT license. The client resolves
assets by validated code, and source tests cover all bundled countries, invalid
codes, IT and AL with opaque names and absent health samples, and CA metadata.
Static inspection confirmed all 250 expected files and PNG dimensions, and
`git diff --check` passed. Builds and tests were not run. Client release and
live UI verification remain outstanding; the separate null-metadata server gap
remains open and was not the observed cause for these two nodes.

## Authorized delivery gates

1. Preserve existing subscription/support/policy identities and both journals;
   compare complete protected state before/after ordinary update.
2. Publish a reviewed task PR to business, AppVersion2.50.11; require exact-head
   hosted checks and the credential-free Business shared-update artifact.
3. Use the canonical WINBRAT lease, fixed identity and fresh CPU/RAM/swap/disk/SDK
   preflight before any private build or live action. No local compilation.
4. Verify hosted artifact digest, SOURCE_SHA, package receipt, PE metadata and all
   archive hashes; build the repair installer with the unchanged full bundle.
5. Verify all250 flag resources with focused/headless tests and actual IT/AL
   server choices through semantic UI; preserve selection and tunnel lifecycle.
6. Exercise ordinary2.50.10 to2.50.11 update, verify exact installed file hashes,
   publish the trusted feed atomically, retain rollback entries and clean only
   task staging after verified archives. Release the worker lease at completion.

Risk is confined to embedded flag rendering/assets and the version bump. Routing,
metadata transport and provisioning contracts are unchanged. Rollback retains the
prior trusted artifact and complete data; never rewind policy journals.
Independent scoped diff review returned[] before build authorization; the added
version bump is mechanical. CI and Windows acceptance remain pending.
