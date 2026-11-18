# Close the confirmed security gaps from the issue queue

Base business at aee8eae29e963dd79fa609b6e14c89e685d1357c. Address the affected SDK-bundled VSTest version (#673),
Android local control and per-app isolation (#689), and the elevated Windows
service-menu launch boundary (#661). The Android report needs independent
verification. Treat a successful configuration test as configuration evidence.
Keep #664 (partially available routing sets) and #666 (desktop API coverage)
separate from security closure unless a privacy or authorization impact is shown.

Retain the .NET 10 target, single-tunnel lifecycle, local authenticated traffic
statistics and liveness. Preserve existing settings and safe repair/support
operations. Keep already issued clients and public releases outside this source
change. Rollback is a revert of the relevant repair on the owning line.

1. Build the affected targets with the fixed SDK in CI, including Android.
2. Verify the actual SDK/test runner and the defensive regression cases.
3. Record the reviewed source changes and observed verification here.
4. Review trust boundaries, lifecycle compatibility and tests independently.
5. Check Android isolation on an authorized Android environment before claiming
   the reported symptom resolved. Use synthetic inputs and ordinary app actions.
6. Compare both maintained branches, existing saved settings and production
   entry points. Record unverified conditions and keep their issues open.

## SDK delivery block

Ship the SDK fix independently while the client trust-boundary fixes undergo review on the runtime branch. Both a review of the Microsoft advisory and the published SDK manifests confirmed affected VSTest 18.6.0 in SDK 10.0.303 and fixed 18.7.1-hotfix-26529-119 in 10.0.404. Root and Android SDK selection, signing, packaging provenance and the hosted test jobs now agree. The Linux and Windows jobs report the actual commit and bundled runner; the Android compile job checks workload compatibility. The Arch reporting unit test and synthetic SDK inventory parser passed locally. The private Mac has only SDK 8.0.125, and the Windows and Linux workers were powered down during the inventory. Hosted exact-commit results remain the merge gate.
