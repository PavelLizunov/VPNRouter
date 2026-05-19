# Phase 6 — CI workflow android.yml + libbox.aar provisioning

**Owner**: Wave 26 agent
**Roadmap ref**: Phase 5 rollup follow-up #1 + #5
**Effort**: 1 day
**Risk**: MEDIUM (touches CI workflow + introduces secret-stored artifact)

## Why

Phase 5 (Wave 23) shipped Android Avalonia 12 + .NET 10 + Android API 36
locally + on phone, but CI for Android remains broken because:
1. CI runner has only .NET 8 — needs .NET 10 SDK install step
2. CI runner has only android-34 — needs platforms;android-36 install
3. CI doesn't have `VPNRouter.Android/Lib/libbox.aar` (private 11.7 MB
   sing-box binding, gitignored)
4. CI doesn't have .NET 10 android workload

Without these, `dotnet build VPNRouter.Android.csproj` returns
"NETSDK1139: target framework moniker 'net10.0-android36.0' is not
recognized" or similar.

Phase 6 closes by:
- Adding install steps to `build-android.yml` workflow
- Provisioning libbox.aar via GitHub repository secret (base64-encoded)
  OR pre-built artifact upload via release asset

## What

### 6-CI-1: Update `.github/workflows/build-android.yml`

Add steps before the existing `dotnet build` step:

```yaml
- name: Install .NET 10 SDK
  uses: actions/setup-dotnet@<sha>
  with:
    dotnet-version: |
      8.0.x
      10.0.x

- name: Install Android SDK platform 36
  run: |
    $SDKMGR = "$env:ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
    & $SDKMGR --install "platforms;android-36" "build-tools;36.0.0"

- name: Install .NET 10 Android workload
  run: dotnet workload install android --skip-manifest-update

- name: Provision libbox.aar
  env:
    LIBBOX_AAR_B64: ${{ secrets.LIBBOX_AAR_BASE64 }}
  run: |
    mkdir -p VPNRouter.Android/Lib
    echo "$LIBBOX_AAR_B64" | base64 -d > VPNRouter.Android/Lib/libbox.aar
    test -s VPNRouter.Android/Lib/libbox.aar  # fail if empty
```

### 6-CI-2: GitHub repo secret `LIBBOX_AAR_BASE64`

User must add the secret via:
- Repo Settings → Secrets and variables → Actions → New repository secret
- Name: `LIBBOX_AAR_BASE64`
- Value: `base64 -i VPNRouter.Android/Lib/libbox.aar | tr -d '\n'`

Document the secret + provisioning command in the brief Outcome + a
`.github/SECRETS.md` doc.

### 6-CI-3: APK upload to release

Existing `build-android.yml` likely already uploads via `gh release
upload --clobber`. Verify the upload step still works post-bump (the
APK path is now `bin/Release/net10.0-android36.0/com.ninitux.vpnrouter-Signed.apk`).

## How

**Step 1**: Read current `.github/workflows/build-android.yml` (if it
exists) or create from `build-linux.yml` template.

**Step 2**: Add the 4 install/provision steps above.

**Step 3**: Verify locally that the secret-decoding step would work
(simulate via env var with the actual b64 content).

**Step 4**: Update `.github/workflows/CLAUDE.md` to document the new
`LIBBOX_AAR_BASE64` secret.

**Step 5**: Add `.github/SECRETS.md` (or update existing) with the
provisioning command for the secret.

**Step 6**: DO NOT commit a real libbox.aar — verify it's still in
`.gitignore`.

## Verification gate

- [ ] build-android.yml updated with 4 new steps
- [ ] Secret name documented (no actual secret committed)
- [ ] SECRETS.md / CLAUDE.md updated
- [ ] libbox.aar stays gitignored (grep `.gitignore`)
- [ ] Workflow YAML lints clean
- [ ] Hook gates pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 7: F-Droid build variant (Android-only) needs a separate
  workflow that builds without Play Store dependencies.
- Phase 7: signed APK with private upload key (currently uses
  debug-signed) for Play Store distribution.
