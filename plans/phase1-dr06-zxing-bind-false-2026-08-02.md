# Phase 1 — DR-06 Android ZXing bindless packaging

**Owner**: Codex

**Branch**: `codex/dr-06-zxing-bind-false`

**Audit ref**: dependency replacement task list DR-06, draft PR #99

**Effort**: 3–5 hours

**Risk**: MEDIUM — APK can compile while failing at the Java/JNI scanner boundary; physical-device E2E is mandatory

**Blast radius**: Android project metadata only; Java ZXing payload and bridge remain packaged

**Rollback**: discard the experiment or revert the implementation commit

## Why

The app calls ZXing only through `QrScanLauncher.java` and a reflective C# JNI
boundary, yet the AAR/JAR currently generate managed C# bindings. The estimated
generated source and AOT cost may be avoidable by setting `Bind=false` while
keeping the Java libraries packaged. The estimate is not acceptance evidence;
the same Release command and a real scanner flow must prove the result.

## What

- Measure the current Release APK, generated binding file count/size, and build
  time from a clean Android tree.
- Set `Bind=false` on the existing ZXing AAR and JAR without changing `Pack` or
  the Java bridge.
- Delete `Transforms/Metadata.xml` only if the bindless build proves it unused.
- Repeat identical measurements.
- Install a correctly signed APK on the physical A101BM and verify scanner
  launch, camera permission, successful decode, result handoff, and logs.
- Push/open a draft PR only after measurable benefit and device E2E success.

```diff
 <AndroidLibrary Include="Lib\zxing-android-embedded-4.3.0.aar"
+                Bind="false"
                 Pack="true" />
```

## How

1. Have Qwen 3.8 map every `Com.Google.Zxing`/`Com.Journeyapps` managed
   reference, Java API use, reflective JNI call, current Bind/Pack setting, and
   `Metadata.xml` role.
2. Capture a clean baseline with an exact command and machine/toolchain details.
3. Apply the two metadata attributes only; rebuild clean and compare raw values.
4. Test a build without `Metadata.xml`; keep its deletion only if build and
   device behavior are unchanged.
5. Reuse the existing Android signing path without printing credentials.
6. Device-test on A101BM via Mac adb. Scan a valid non-secret test payload and
   inspect logs for `ClassNotFoundException`, `NoClassDefFoundError`, and JNI
   failures.

### Tests written

- None planned: the critical contract is packaging/runtime behavior and requires
  the physical device.

### Verification approach

- Release builds before/after from clean `bin`/`obj` using the same toolchain.
- Exact APK bytes and generated binding file count/bytes recorded.
- No managed source reference to generated ZXing namespaces.
- Physical scanner/permission/decode/handoff E2E and log scan pass.

## Verification gate

- [ ] **Gate 1 — Build clean**: bindless Android Release build has 0 errors.
- [ ] **Gate 2 — Device E2E**: A101BM scan flow and JNI/log checks pass.
- [ ] **Gate 3 — Measurements**: exact before/after build time, APK size, generated file count/bytes recorded.
- [ ] **Gate 4 — Qwen/self-review**: final packaging diff has no blocker.
- [ ] **Gate 5 — Push/PR**: permitted only after Gates 1–4 pass.
- [ ] **Gate 6 — CI**: final pushed head is green.

## Outcome

To be filled after the experiment.
