# Verification branch preservation check

Read-only against main0bcc8166510310d39e442b09a3e0e804eb94dd01. No refs deleted.

NIGHT RED tip7f21286d7a14414de818510ccea04b7014caf776 has eight unique non-merge test commits. All eight changed test-file blobs are byte-identical to main: NightBaselineEndpointTests, NightBaselineFailoverTests, NightBaselineFirewallTests, NightBaselineOwnershipCharacterizationTests, NightBaselineRegressionTests, NightBaselineStatsTests, NightBaselineStopCharacterizationTests, NightBaselineTelemetryWiringTests. Test contents are preserved, but the historical composition with unfixed product is unique and useful as RED reproducibility evidence. Do not equate identical fixtures with preserved baseline execution tree.

FakeIP RED c36e50349f5810f2782cb4535119723b6dd0d2ae and packaging RED0f85cbfeaf0d2030285c0ab44df1d64d13aefaef contain fixture blobs different from current main; semantic comparison not completed. Combined1fe721a935f95986d60ea9bea3e76a87f4c11adf includes two unique composition commits and older product snapshot, superseded for current acceptance but historical evidence retained. Proposed disposition: verified self-contained Git bundle/recovery index if owner chooses archived evidence instead of visible branches; no archive creation or deletion authorized by this note. Existing approved metadata backups do not substitute for Git object bundles.
