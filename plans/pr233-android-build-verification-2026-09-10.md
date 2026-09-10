# PR233 bounded Android build verification

Source c81c514e3cc2e103261155a1e7eb3f25e927b2c9 on linux-worker, detached task checkout /home/tester/vpnrouter-verification/pr233-bf67072f. Initial result: FAIL before compilation/package creation. After separately approved platform-tools installation, bounded unsigned APK build and artifact checks PASS; see follow-up below. No runtime/device or release acceptance implied.

Preflight: tester/debian-xfce, no conflicting dotnet job, available RAM6731MiB, disk43GiB. Existing isolated SDK10.0.301 / Android workload36.1.2, JDK17 and SDK36 used; no infrastructure provisioned.

## Signing boundary

Inspected installed SDK targets and preprocessed project. Default PackageForAndroid invokes Build, which appends _Sign in ordinary CLI mode. Used global BuildingInsideVisualStudio=true to suppress that append while preserving Build;_CopyPackage. Task-owned imported unsigned-guard.targets registers Error BeforeTargets _Sign;SignAndroidPackage;_ResolveAndroidSigningKey;_CreateAndroidDebugSigningKey;_CreateUniversalApkFromBundle. It rejects signing rather than overriding or silently skipping behavior. Preprocessed XML confirms guard imported and signing tasks reside in protected SDK targets. This is an IDE-conditioned SDK-target adaptation, not unchanged release-workflow validation; complete post-restore target reachability not proven because build stopped at SDK resolution.

## Approved platform-tools follow-up: PASS

Owner explicitly approved platform-tools via pr233-platform-tools-consent. Vendor repository2-1.xml reports stable37.0.1, android-sdk-license, Linux URL https://dl.google.com/android/repository/platform-tools_r37.0.1-linux.zip,9054187 bytes, vendor SHA1 477254aa5f903c15cf51001717bdf347fb6b53e0. sdkmanager installed only platform-tools with stdin closed, existing approved license; bash-213 exit0. source.properties confirms37.0.1. No adb/fastboot/device commands executed. Archive checksum validation delegated to sdkmanager; no independently measured archive SHA256 claimed.

bash-214 repeated exact previous guarded Release ARM64 command with serial restore and new diagnostic filenames platform-tools-build.log/platform-tools-console.log. Exit0. Installing the approved platform-tools resolved observed XA5300 without project changes or resolver bypass.

APK: VPNRouter.Android/bin/Release/net10.0-android36.0/android-arm64/com.ninitux.vpnrouter.apk,32824867 bytes; SHA25633473146249a7ea3f693541cf85df421eedd54351cc4b75eede820c60a91d6a1.

- ZIP testzip returned None (all member CRCs valid); only ARM64-v8a native ABI.
- aapt2 badging: com.ninitux.vpnrouter,versionName2.49.3,versionCode2049003,target36. Initial script expected minSdk in badging but tool omitted it; xmltree directly confirms minSdk23,target36.
- APK lib/arm64-v8a/libbox.so byte-identical to verified AAR jni/arm64-v8a/libbox.so; extracted SHA2567322714fd62cd0e975d6d3beb5b232c4e32ebf215b86e849270f17e8f5592f14.
- apksigner verify initially exited127 because launcher needs java on PATH despite JAVA_HOME; repeated with existing JDK17/bin PATH, exit1 with DOES NOT VERIFY / ERROR: Missing META-INF/MANIFEST.MF. This is expected unsigned evidence, independent of successful ZIP integrity.
- Diagnostic log contains no executed Task AndroidSignPackage, AndroidApkSigner or AndroidCreateDebugKey. Fail-closed signing guard remained imported throughout successful build. No Signed.apk output.
- Optional Slipstream JNI absent: not full DNS-tunnel feature parity. No device, emulator, VPN, signing or deployment test performed. IDE-conditioned guarded package is not identical to release workflow or production-signed APK.

All jobs collected. Artifacts/logs retained for parent's final collection and exact task cleanup; shared SDK and caches preserved. Sections below retain initial failure history, superseded by this follow-up.

## Execution

Pinned libbox1.13.10 fetched from public tooling release into ignored VPNRouter.Android/Lib/libbox.aar, SHA256239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6 verified. Both ZXing payloads already tracked. Fresh Android bin/obj absent before first run.

Release ARM64 PackageForAndroid, singular/plural RID android-arm64, APK format, blank signing properties, explicit AndroidSdkDirectory/JavaSdkDirectory, -m:1, guard import and diagnostic file logger.

- bash-211 failed restore NU1301: Azure dotnet-experimental feed returned HTTP500 concurrency/IP rate limit. No feed was removed or ignored.
- bash-212 one retry with RestoreDisableParallel=true restored successfully, then failed XA5300 in _ResolveSdks / Xamarin.Android.Tooling.targets58: Android SDK directory could not be found.
- Diagnostic confirms AndroidSdkDirectory AND ResolveSdks task AndroidSdkPath equal existing /home/tester/.local/share/vpnrouter-build-tools/android-sdk. Directory contains platforms, build-tools, cmdline-tools, licenses, .knownPackages and .temp; platform-tools absent. Missing platform-tools is a plausible SDK-recognition prerequisite, not conclusively proven from available error text. No dummy directories, SDK override bypass, new installation or further retries performed.

No APK outputs exist. ZIP/manifest/API/ABI/signature postchecks cannot run. No signing or key-generation stage reached (failure occurred first at SDK resolution); no production credentials, app deployment, VPN/device/emulator/NDK used. No project source edits or Git operations.

Task-owned diagnostics retained for parent collection: verification-output/android-unsigned/{effective.xml,unsigned-guard.targets,console.log,build.log,retry-console.log,retry.log}. Android obj restore files and ignored libbox are task artifacts; parent should collect needed receipts then remove exact task-owned artifacts with checkout cleanup, never shared NuGet caches. All background jobs collected. Further action: diagnose SDK-recognition prerequisites read-only, obtain explicit approval before provisioning any missing package, rerun exact SHA with signing guard and perform APK postchecks only if package exists.
