# Security Review Report: VPNRouter 2.50 (main b101a7e2)

**Date:** 2026-09-16
**Review Target:** `0bcc8166..b101a7e2` (recent 2.50 product changes) plus reachable Core/App/Android/packaging sinks named by leftover OPEN-DEFECTS P1s
**Overall Risk Assessment:** HIGH
**Merge Recommendation:** CONDITIONAL APPROVAL (already on `main`; do not cut stable until new P1s below are fixed or waived)

---

## 1. Executive Summary

| Severity | Confirmed | Reasoned Hypothesis | Total |
|---|---|---|---|
| CRITICAL | 0 | 0 | 0 |
| HIGH | 2 | 0 | 2 |
| MEDIUM | 5 | 2 | 7 |
| LOW | 4 | 1 | 5 |

### Key Review Metrics
- **Files Inspected:** recent 2.50 product diff (~97 product files) plus current NIGHT/release P1 call sites
- **Security Regressions Detected:** 0 reintroduced CVEs; several 2.50 patches hold. New/remaining HIGH: subscription exception URI leak; auto-update size-only SHA fallback
- **Untested Security Paths:** live Serilog file-sink, live GitHub update, WINBRAT, Android device
- **Recommendation Summary:** Recent OpenUrl / ArgumentList / Zapret RunTests patches are real. Do not treat them as complete: `cmd.exe` OpenServiceMenu, Flowseal probe `Arguments`, exception-object logging, and missing-SHA extraction remain.

---

## 2. Scope and Changes Reviewed

**Comparison Range:** `0bcc8166..b101a7e2` (HEAD `b101a7e232376b32b855b9e7c4f53eaeaeaf7ad8`) plus current-source recheck of leftover ledger P1s. Working tree clean of product edits (untracked: `.dsh/performance-autoresearch/`, `proxy_sources_2026-09-13.json`, `vpn_issue_report.md`).

| File | Risk Level | Sensitive Elements | Reachability Summary |
|---|---|---|---|
| `VPNRouter.App/Views/AboutWindow.axaml.cs` | LOW | OpenUrl HTTP(S) gate | Hardcoded GitHub URL |
| `VPNRouter.App/ViewModels/MainWindowViewModel.cs` | MEDIUM | OpenUrl, OpenFolderInExplorer, taskkill | UI commands; OpenUrl only hardcoded https |
| `VPNRouter.Core/Services/ZapretActions.cs` | HIGH | cmd.exe / powershell spawn | UI + public `OpenServiceMenu(path)` |
| `VPNRouter.Core/Services/ZapretAutoStrategy.cs` | MEDIUM | powershell `-File` string, probe logs | UI one-tap probe |
| `VPNRouter.Core/Services/SubscriptionFetcher.cs` | HIGH | URL + Exception logging | User subscription URL |
| `VPNRouter.Core/Services/PolicyHttpClient.cs` | HIGH | TimeoutException embeds URI | Shared HTTP |
| `VPNRouter.Core/Services/UpdateChecker.cs` | HIGH | SHA skip, zip/tar extract | In-app update |
| `VPNRouter.Core/Services/Diagnostics/DiagnosticsRedactor.cs` | MEDIUM | Separatorless secret regex | Logs / crash |
| `VPNRouter.Android/AndroidApp.FreeConfigs.cs` | MEDIUM | URI parse, apply | Android UI |

---

## 3. Security Findings

### HIGH Subscription fetch exceptions log the raw request URI

- **Status:** Confirmed
- **Location:** `VPNRouter.Core/Services/SubscriptionFetcher.cs:117`; `VPNRouter.Core/Services/PolicyHttpClient.cs:157-158`
- **Commit:** present at HEAD `b101a7e2` (not introduced by the 2.50 OpenUrl patches)
- **Reachability:** Authenticated local user / any configured subscription URL
- **Test Coverage:** Incomplete (`SubscriptionUrlRedactionTests` asserts `RenderMessage()`, not `logEvent.Exception`)

#### Description
Template `{Url}` is passed through `CanaryPolicy.RedactUrl`, but `logger.Error(ex, ...)` still persists `ex.ToString()`. `PolicyHttpClient` puts the full `request.Uri` into `TimeoutException.Message`. Tokens in path/query therefore land in `vpnrouter*.log`.

#### Attack Scenario
1. **Attacker:** Anyone who can read local logs or a shared diagnostic dump
2. **Action:** Subscription fetch times out (or other HTTP exception whose message includes the URI)
3. **Execution Path:** `FetchAsync` -> `PolicyHttpClient` timeout -> `SubscriptionFetcher` catch logs `ex`
4. **Impact:** Provider token / path secret written to disk logs

#### Remediation
Log `ex.GetType().Name` only, or scrub `ex.Message` with `CanaryPolicy.RedactUrl` / `CrashReporter.ScrubSecrets` before logging. Extend tests to assert `LogEvent.Exception`.

---

### HIGH Auto-update can extract with no SHA256

- **Status:** Confirmed
- **Location:** `VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs:120-135`; `VPNRouter.Core/Services/UpdateChecker.cs:304-348`
- **Reachability:** In-app GitHub upgrade path (downgrade `ListStableAsync` already requires SHA)
- **Test Coverage:** Staging tests allow `FullChecksumUrl = null`

#### Description
`CheckAsync` treats companion `.sha256` as best-effort. `DownloadAndStageAsync` "degrades to size-only" when both inline digest and sidecar URL are absent. Lite updates force `expectedSha = null`. A matching-size payload then extracts.

#### Attack Scenario
1. **Attacker:** MITM or compromised GitHub/CDN asset without a sidecar
2. **Action:** Ship a same-size zip/tar
3. **Impact:** Unsigned tree extracted into install staging; combined with missing member/symlink policy this is the trust root for the next binary

#### Remediation
Fail closed when `AssetSha256` is missing on the upgrade path, matching `ListStableAsync`. Reject `..` / absolute / symlink archive members before copy.

---

### MEDIUM OpenServiceMenu still concatenates `cmd.exe` Arguments

- **Status:** Confirmed
- **Location:** `VPNRouter.Core/Services/ZapretActions.cs:581-595`
- **Reachability:** Production UI uses trusted `ZapretDir\service.bat`; public optional path widens the API (`ZapretActionsTests`)

Denylist blocks `\r\n&|^<>%" ` then still does `Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{servicePath}\"\"") { UseShellExecute = true, Verb = "runas" })`. Sibling `RunTests` already uses `ArgumentList`.

#### Remediation
Start `service.bat` via `ArgumentList` (or drop `cmd /k`). Keep elevation if required.

---

### MEDIUM Flowseal probe still interpolates `-File` into Arguments

- **Status:** Confirmed
- **Location:** `VPNRouter.Core/Services/ZapretAutoStrategy.cs` `RunFlowsealProbeAsync` (scriptPath interpolated into `Arguments`)
- **Reachability:** Production path is `ZapretUpdater.ZapretDir`; method is public and takes `zapretInstallDir`

#### Remediation
Mirror `ZapretActions.RunTests` ArgumentList (`-NoProfile`, `-ExecutionPolicy`, `Bypass`, `-File`, path).

---

### MEDIUM Crash reports skip the key=value redactor

- **Status:** Confirmed
- **Location:** `VPNRouter.Core/Services/CrashReporter.cs:111,135` vs `DiagnosticsRedactor.RedactLogText`

`WriteReport` uses `ScrubSecrets` only. Short `password=hunter2` / `Authorization: Bearer` in the last 200 log lines can survive. Separatorless patch added literals (`clientsecret`, `accesstoken`) but `idtoken` / `appsecret` still leak.

#### Remediation
Route crash tails through `DiagnosticsRedactor.RedactLogText`. Generalize separatorless matching.

---

### MEDIUM Android / aggregator still VLESS-only after 2.50 multi-protocol

- **Status:** Confirmed
- **Location:** `VPNRouter.Android/AndroidApp.FreeConfigs.cs:1110`; `VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs:132`; `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs:133`
- **Impact:** Availability / failed apply, not RCE. Desktop apply uses `ToVlessServerEntry()` -> `ServerUriParser.Parse`.

---

### MEDIUM ApplyFreeConfig false-green on non-Connected two-phase outcomes

- **Status:** Confirmed
- **Location:** `VPNRouter.App/ViewModels/MainWindowViewModel.FreeConfigs.cs:229-239`
- **Impact:** UI reports Connected after swallowed start exceptions; `ToggleConnectionAsync` correctly greens only on `TwoPhaseStartOutcome.Connected` (`MainWindowViewModel.Connection.cs:407-441`)

---

### LOW RemoteVersionChecker redacts cache-hit only

- **Status:** Confirmed (production `ownerRepo` is a const)
- **Location:** `VPNRouter.Core/Services/RemoteVersionChecker.cs:118,125,136` unredacted `{Repo}`

---

### LOW OpenHostsEditHelpers concatenates explorer `/select`

- **Status:** Confirmed leftover pattern; path is `%System%\drivers\etc\hosts`, not user-controlled
- **Location:** `VPNRouter.Core/Services/ZapretActions.cs:296-297`

---

## 4. Test Coverage and Unverified Paths

| Modified Function / File | Missing Test Scenarios | Risk Implication |
|---|---|---|
| `SubscriptionFetcher` catch | `logEvent.Exception` contains token | Silent log leak |
| `UpdateChecker.DownloadAndStageAsync` | Missing SHA must refuse | Size-only install |
| `ApplyFreeConfigAsync` | StartTaskCompleted / faulted startTask must not set `IsConnected` | False-green |
| `AndroidApp.OnFreeConfigsUseClicked` | hysteria2/ss URI apply | Apply fails / no coverage (`AndroidAppCharacterizationTests` deleted) |
| `ZapretActions.OpenServiceMenu` | ArgumentList vs Arguments | Injection leftover |

---

## 5. Coverage Boundaries and Limitations

- **Reviewed Scope:** 2.50 product commits since `0bcc8166`, current source of named NIGHT/release P1s, ProcessStartInfo sites in App/Core Zapret/About/OpenUrl
- **Excluded Areas:** Live WINBRAT, live GitHub publication, Android device, Zapret/TgProxy binary payloads, `tools/zapret/` blob
- **Mechanical tests:** Not executed here (`dotnet` SDK absent on this host). Claims are source-traced, not CI-green in this session
- **Finding-Free Notice:** Patches #283/#285/#289 hold on their assigned sinks. That does not prove the product is secret-safe or fail-closed on update

---

## 6. Investigated False Positives

- **`AboutWindow.OpenUrl`:** const `https://github.com/PavelLizunov/VPNRouter` plus scheme gate. No user input.
- **`MainWindowViewModel.OpenUrl`:** scheme gate + `CanaryPolicy.RedactUrl` on block/fail; callers pass hardcoded https.
- **`taskkill`:** ArgumentList `/F /IM winws.exe`. No user input.
- **`OpenFolderInExplorer`:** ArgumentList + `UseShellExecute=false`.
- **`NIGHT-01 KillAll`:** `TgProxyManager.KillAll` / `KillByPort` are empty no-ops at `TgProxyManager.cs:597-610`. Autostart `IsAnyRunning` is occupancy fail-closed skip, not a kill.
- **`NIGHT-02..08`:** Current source implements the approved repairs (see review notes). Ledger checkboxes stay open until owner acceptance of the #240 integration, per `OPEN-DEFECTS.md:301-305`.
- **`build.ps1 --latest` P1:** stale. `build.ps1:851-881` is Android keystore skip text; `ReleaseToolingContractTests` forbids `--latest` in `build.ps1`.
- **`sign-android.yml` legacy sign P1:** current file is 25 lines and `exit 1`s. Ledger citation of lines 59-134 is stale.
- **`verify-release-integrity.yml` AppImage-before-hash:** header now states payloads are never executed; inspection is hash/sidecar based.
- **`ToLowerInvariant` on `process_name`:** false positive in ConfigGenerator; only RoutingAppsMode / protocol discriminators are lowercased.
- **`GetProcessesByName(...).Length` in product:** no remaining product hits (tests and comments only).
