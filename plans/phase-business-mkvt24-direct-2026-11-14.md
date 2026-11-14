# Business mkvt24.ru direct routing

Owner request: route mkvt24.ru directly in the Business edition. Base: business at 8fe6c19d; task branch: dsh/business-mkvt24-direct-20261114, PR target business.

Scope: add one enabled domain_suffix direct rule to the embedded Business preset and extend the existing generated-config theory to exercise this domain in split/full modes, including local DNS and precedence over proxy process rules. Preserve other rules, provisioning, version and update channel. Rollback: revert this scoped change.

Verification: JSON parsing, unique enabled rule and conflict checks, scoped diff; existing BusinessDefaultsTests and Business CI publication/full applicable suites at the exact pushed SHA. No workstation build, packaging or test run. UI/characterization gates are N/A (no UI/API/refactor); live Windows routing and delivery remain unverified until an authorized build/deployment. This small preset change has no independent agent review.

Evidence: VPNRouter.App.csproj embeds business-rules.json as VPNRouter.Business.Rules; BusinessBootstrap reads it and BusinessDefaults.Apply replaces CustomRules at startup. The base contains no explicit mkvt24.ru rule; no new runtime mechanism is required. Base GitHub checks: seven successful, no failures; no hash-bump suggestion file.

Outcome: one enabled direct suffix added; all 43 previous rules unchanged. JSON, uniqueness, resource wiring and diff checks PASS. The existing split/full theory now asserts direct mkvt24.ru routing and local DNS with geo bypass disabled. Exact-head C# test/build results will be attached to the task PR; they have not run locally. No merge, release, installer or feed deployment authorized.
