# PR233 platform verification progress

Tested source: c81c514e3cc2e103261155a1e7eb3f25e927b2c9. Status: bounded pre-merge verification completed with the explicit non-release limits below. Final documentation-head CI and SHA-guarded merge remain pending.

Android follow-up PASS after separately authorized platform-tools37.0.1: guarded unsigned Release ARM64 APK produced. See pr233-android-build-verification-2026-09-10.md. Parent independently read back exact checkout SHA, APK SHA25633473146249a7ea3f693541cf85df421eedd54351cc4b75eede820c60a91d6a1,32824867bytes, valid ZIP, ARM64-only ABI and no Signed.apk. Agent recorded minAPI23/target36/version2.49.3, pinned libbox parity and absent signature. No Slipstream JNI/device/runtime acceptance. Earlier pending paragraphs below describe the preparation sequence, superseded by this result.

Lead accepts the reviewed product scope for the already owner-authorized merge once final head CI passes: Windows native/CI, Linux verification tar, macOS source-equivalent ZIP/DMG and guarded legacy Android APK. This does not accept or waive external release gates, deb/AppImage/release workflow identity, production signing, installed-runtime replacement or live VPN behavior. Final independent source review PASS at c81c514e retained; subsequent changes are receipts only.

## Completed checks

All five PR checks passed: dotnet workflow34518956436 (Ubuntu tests, Windows characterization/release contracts and Windows Go), fingerprint34518956384 and Windows update34518956404. Exact log totals: Ubuntu3446 passed/88 skipped/3534 total; Windows characterization440 passed/8 skipped/448 total. Corrected PowerShell block count now passes actual Windows5.1 parser gate; not merely a source expectation update.

Windows native log inspected: four Native_MigratedFakeIp_Check cases explicitly Passed (legacy both families, disabled old object, typed already, legacy IPv4). Cronet hash c7434cfa93c3041321dd19111c4de6c52b8a9531a65661ba45425d3c51ec69e2 confirmed in both Windows ZIPs; missing-DLL negative control exited1 and named libcronet. Updater log reached helper done; overall workflow passed. These are disposable CI native decoder/package/update witnesses, not live VPN/dataplane or WINBRAT release acceptance.

Linux native worker: SDK10.0.301 publish VPNRouter.App -c Release -r linux-x64 --self-contained to task-owned verification-output/publish-linux-x64 succeeded (bash-208). Checkout /home/tester/vpnrouter-verification/pr233-bf67072f updated only after prior job ended and clean tracked tree verified, detached at c81c514e despite historical directory basename.

macOS native worker: fresh detached checkout /Users/slovn/vpnrouter-verification/pr233-c81c514e, explicit user-local SDK10.0.301, publish -c Release -r osx-arm64 --self-contained into verification-output/publish-arm64 succeeded (bash-207). Both builds emitted warnings; no warning-free claim. No app launch or installation.

## Linux verification archive

At exact c81c514e, bash-209 constructed a task-owned verification tar from the previously published Linux payload plus pinned sing-box1.14.0-vpnctl.3 and legacy upstream1.13.14 libcronet.so. Both vendor archives passed committed SHA256 pins before extraction; archive member paths/types validated (only relative non-traversing files/directories). No downloaded binary executed. Both components identified as ELF x86-64. Tar reopened successfully with236 entries and required App/App.dll/sing-box/libcronet.so files present.

- sing-box SHA256: 973e453dc835ec07b53e950c97eb956fedb436434619e178c5dace0568cde0f6
- libcronet.so SHA256: dc7293a929dffa695aae1a89555e7366158fa0a3f40bbe3012d445bc05c99672
- verification tar SHA256: 04f8e7c9464d1c5843c04cb94c2bd8246fcc88b41004bed638aade3fc9cfac2e

Worker-relative output: verification-output/linux-package/VPNRouter-v2.49.3-linux-verification.tar.gz. This is a verification archive, NOT release tar identity, deb/AppImage verification, runtime loading or VPN evidence. Task artifacts retained, no shared cleanup.

## macOS verification packages

At exact c81c514e, bash-210 produced task-owned ZIP and DMG from the successful ARM64 publish, committed Info.plist template (version2.49.3), icon and installation guide. Pinned helper ZIP SHA256c71bf2fab29a00d70f8706eb2f71643e35438769cbbacdd566d7c0e6058be3b1 passed before extraction with path/type validation. Helper is universal Mach-O x86-64/ARM64; apphost remains ARM64. Stage includes app, guide, Applications and Terminal symlinks. plutil lint, hdiutil verify and unzip -tq all succeeded; no app/core execution, installation or deployment.

- bundled helper SHA256: f5a931da2c0a9f841decc25e9efe61bf052e5dfd706350210d17dae46324f507
- DMG SHA256: 45bbb450afed081ef0ce701e97c0316087005a76546d7cdee91f5c3c60c89a11
- ZIP SHA256: 3d1f265a14cdde2ff82ef1a2e76258e9c5d020f8f139529a3288c9277fc59524

Outputs under verification-output/mac-package/ on mac task checkout. Source-equivalent packaging only: owned paths, verification artifact/volume names, explicit SDK, Python copy/plist extraction, no quarantine removal, shared cleanup, -ov, retry or shared-volume detach. hdiutil may use temporary internal mounts during creation; no user application volume mounted for installation or testing. This is not unchanged build-mac.sh, notarization, Gatekeeper/runtime or release identity proof.

## Packaging scope and next steps

Independent read-only analysis confirms build-mac.sh cannot safely run unchanged on persistent worker: shared /tmp deletion/download/extract paths, Homebrew PATH override and shared-volume detach retry. Use reviewed source-equivalent bundle/plist/stage operations in owned output only, with explicit SDK and mandatory pinned helper hash; label source-equivalent, not unchanged-script validation. No new script implementation selected. Native Linux workflow steps rather than Windows-oriented build-linux.ps1 are appropriate for worker packaging.

Completed bounded desktop package evidence: Linux verification tar and macOS verification ZIP/DMG above. Linux deb/AppImage and unchanged release-script behavior remain unverified, not silently equated with these outputs. Remaining before #233 acceptance: Android unsigned ARM64 APK with pinned libbox1.13.10, plus final exact-head review and explicit disposition of remaining package/runtime limitations. Inspect installed Android PackageForAndroid target dependency closure before execution to ensure no signing/debug-keystore generation. AndroidKeyStore=false alone is NOT unsigned. No emulator, NDK, production keystore, release tag, deployment or live VPN. Optional missing Slipstream JNI must be disclosed, not silently called full-feature Android parity.

Worker-side inspection of installed Android36.1.2 targets found that PackageForAndroid depends on Build; Microsoft.Android.Sdk.BuildOrder.targets appends _Sign to BuildDependsOn when BuildingInsideVisualStudio is not true. Therefore the previously proposed PackageForAndroid invocation is NOT unsigned under default CLI settings and was not executed. _Sign also reaches _CreateAndroidDebugSigningKey via _ResolveAndroidSigningKey. Determine and verify a signing-excluding build route before proceeding; no target-name-based unsigned assumption.

Read-only msbuild -getProperty under Release/ARM64/BuildingInsideVisualStudio=true confirms effective BuildDependsOn no longer includes _Sign; PackageForAndroid still depends on Build;_CopyPackage. Subsequent guarded build reached restore and SDK resolution, then failed XA5300 before compilation/package creation. See plans/pr233-android-build-verification-2026-09-10.md for diagnostics and Error-before-signing guard. Owner explicitly approved adding only platform-tools via pr233-platform-tools-consent; independent worker is installing/verifying that package and retrying exact source. No APK pass or proven root cause yet. This is an SDK-target adaptation, not unchanged CLI workflow verification.

Desktop artifact readback at exact c81c514e: Linux apphost ELF64 x86-64 SHA25661659a5f7fc8b46c943a2d9c92b257fc37bbfe2fee7588195478c6725d4ca445; Linux App.dll0216509ad5b99edf5115b04f5cefd4610334b0fc3baa4d31b834dfbc3a1d0b5b. macOS apphost Mach-O64 ARM64 SHA256a873941081d800e6169ff2afd482d04d6602d9356f27b096dc099ad636086793; App.dll0371679d328cfe52003ae6061188fab5ad7c2c9aab8c3c84d3ab9e5cb5033be7. No executable launched for this readback.

Published self-contained macOS output may involve SDK ad-hoc signing; no production signing/notarization or credential use was invoked. Retain task artifacts until receipts collected, then remove exact task-owned verification outputs per worker contract. Broader combined-main regression/anti-slop/performance work follows completion of merges per owner ordering.
