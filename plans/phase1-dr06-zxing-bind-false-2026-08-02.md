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

- [x] **Gate 1 — Build clean**: bindless Android Release build has 0 errors.
- [x] **Gate 2 — Device E2E**: A101BM scanner launch, permission deny/grant,
  camera preview, cancel return, successful decode, result handoff, and JNI/log
  checks pass.
- [x] **Gate 3 — Measurements**: exact before/after build time, APK size, generated file count/bytes recorded.
- [x] **Gate 4 — Qwen/self-review**: exact `qwen3.8-max-preview` final review
  passed; deleting `Metadata.xml` is safe because every rule targeted only the
  disabled ZXing bindings.
- [ ] **Gate 5 — Push/PR**: permitted only after Gates 1–4 pass.
- [ ] **Gate 6 — CI**: final pushed head is green.

## Outcome

The clean .NET 10 Release experiment and physical-device E2E validate the
reduction. The change is eligible for a commit and draft PR.

| Measurement | Baseline | Bindless final | Delta |
|---|---:|---:|---:|
| Build wall time | 99.720 s | 88.159 s | -11.561 s (-11.6%) |
| Signed APK | 88,367,134 B | 87,199,774 B | -1,167,360 B (-1.3%) |
| Generated ZXing C# | 166 files / 1,529,826 B | 0 / 0 B | -166 / -1,529,826 B |
| `api.xml` | 1,589,405 B | 269,830 B | -1,319,575 B |
| Build warnings | 208 | 133 | -75 |

The SDK auto-imports all AAR/JAR files. Therefore child `Bind=false` metadata
on another `Include` was not enough: it created a duplicate while the original
auto-imported item stayed `Bind=true`. The working minimal change uses
`AndroidLibrary Update=... Bind=false`; evaluated metadata then shows one ZXing
AAR and one ZXing JAR, both `Bind=false` and `Pack=true`. `Metadata.xml` became
unused and was deleted. APK analysis confirms that `QrScanLauncher`,
`CaptureActivity`, and `QRCodeReader` remain in DEX and the merged manifest
still declares `CaptureActivity`.

A separate debug-signed package (`com.ninitux.vpnrouter.dr06test`) was installed
beside the production app on A101BM without touching production data. Permission
deny and grant, live camera preview, back/cancel return, and a log scan for
`ClassNotFoundException`, `NoClassDefFoundError`, `UnsatisfiedLinkError`, JNI,
and fatal exceptions all passed. The device then decoded the QR in 74 ms,
returned the text through the reflective Java/C# boundary, persisted the
`DR06-E2E` server, requested connection, and completed the Android VPN-consent
flow. The UI not copying the result into `_serverInput.Text` is the expected v3
one-step scan behavior: save and connect immediately.

The later service-start failure belongs only to the side-by-side test package:
its temporary application ID was `com.ninitux.vpnrouter.dr06test`, while the
service class keeps the production Java package. The stock application preserves
the required application-ID/package invariant, and DR-06 does not touch service
registration. No product fix is warranted. Production-key install/update
verification remains a shared release-signing gate, not a DR-06 correctness
gate.
