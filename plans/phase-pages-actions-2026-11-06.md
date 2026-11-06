# Preserve Pages while retaining only main and business branches

## Why, authority and scope

The owner requests only main and business branches. gh-pages currently serves the installation site and signed APT repository, so deleting it requires preserving publication first. This narrow main-based maintenance PR migrates hosting to GitHub Actions; it does not merge Business product changes into Standard main, publish a product version, change tags/releases, or rebuild the issued private Business installer.

## What and invariants

Rename the canonical publisher to publish-pages.yml with a distinct name/ID. Keep the complete existing admission/exact-source/stable-selection/GPG/DEB/signed-index/site-generation behavior. Replace orphan branch publishing with ordinary staging, artifact upload and a dependent environment-bound Pages deploy. Update the strict verification workflow mappings and owning release references.

Add a one-time manual main-only migration pinned to the existing gh-pages commit93abe24c28bef534d6bfe0f0f211c2fb9600f48f. Validate archive entries,37regular files/56800902bytes, domain and existing APT signatures/hashes before upload. The official upload action omits only the empty .nojekyll marker; Actions does not run Jekyll. No private signing key is used by migration and no snapshot binaries enter source.

## How, risk and rollback

Retain gh-pages until successful deployment and comparison of every36public file's byte count/Git blob digest. Preserve the custom domain vpn.ninitux.com and HTTPS when switching build_type from legacy to workflow. Add narrowly scoped main and v* tag environment policies. Confirm distinct replacement workflow IDs, disable legacy publisher263772941, and confirm no queued/running legacy publisher before deleting gh-pages. Disable the one-time migration after acceptance. Do not change tags/releases or introduce repository-wide branch restrictions.

Save the original Pages/settings/environment state before mutation. Before deletion, rollback can restore build_type legacy and source branch gh-pages/path / while explicitly preserving cname and https_enforced; re-enable the legacy publisher only if abandoning this migration. The verified private complete-history bundle preserves the site/source refs after deletion. Never delete the site before acceptance.

## Verification and six gates

1. Build: exact-head ordinary PR CI; no SDK/product builds on Harness or Omarchy.
2. Tests: AptReleaseWorkflowTests plus discovered PR suites; preserve strict release-verifier semantics. Static YAML/bash/Python checks and12actual admission guard cases already pass in preparation; C# and deployment execution remain pending.
3. Documentation: owning workflow/packaging/release/tool references, this outcome and branch-consolidation records.
4. Review: three independent native correctness/security/test lenses reviewed all14effective files against actualmain1c2ced13; no confirmed P0/P1/P2. No external repo-hygiene model. Known limits: structural rather than executed tar/GPG/deploy tests, and generic strict-verifier fixtures do not isolate the new dependent deploy.
5. Windows/UI: no product/UI change; new direct old5-to8 update passed separately on WINBRAT, all278payload files matched, profile retained, cleanup complete and lease released. That acceptance applies to immutableed2c570c product source, not a rebuilt package.
6. Integration: require successful migration jobs, actual public byte comparison, unchanged CNAME/HTTPS and disabled retired publisher before branch deletion. Carry this accepted narrow maintenance into business.

## Outcome

In progress. Source proposal exactbase1c2ced13, patchSHA256904da28477969d1aff81b1d0a56adb1513ad18df479082aedbf6369456bec534. Official full action pins verified in preparation: upload-pages-artifact7b1f4a764d45c48632c6b24a0339c27f5614fb0b and deploy-pagesd6db90164ac5ed86f2b6aed7e0febac5b3c0c03e. Existing checkout pin retained. No GitHub Pages setting changed at brief creation.
