# Shared Business update packaging

Run on an authorized Windows build worker or exact-source CI with the pinned SDK,
Go and PowerShell 7.2 or newer already available:

```powershell
./tools/build-business-update.ps1 -OutputDir <new-task-output-directory>
```

The script reuses `build.ps1` and its verified native dependency packaging. It
sets `VPNRouterEdition=Business` and `BusinessSharedUpdate=true`, excludes the
consumer GUI/CLI/Service entry points, adds the Russian geo rules and emits:

- `VPNRouterBusiness-update-v<version>-win-x64.zip`, containing only `app/`;
- its `.sha256` sidecar and `source-sha.txt`;
- `app/vpnrouter-business-update.json` with schema 1, Business edition, win-x64
  architecture, the source version and external provisioning.

The package validation tests inspect the actual Core/App PE metadata, the full ZIP
layout and required runtime files before a SHA sidecar is written. No application
binary is launched for validation. Only a validated ZIP and sidecar are delivery
artifacts; `payload/` is task-owned staging.

Shared mode refuses `BusinessConnectionFile` and cannot embed a connection resource.
The existing `tools/build-business.ps1` remains the private first-install bridge:
without shared mode, Business builds still require its external connection file.
Existing installed Business provisioning and the application's update admission
rules determine whether a shared update can be applied. This ZIP does not provision
a new employee device.

The trusted provisioning field `business.updates_url` points at a JSON manifest.
Example data (replace the example URL, length and checksum at publication):

```json
{"schema":1,"edition":"Business","architecture":"win-x64","version":"2.50.6","asset":"VPNRouterBusiness-update-v2.50.6-win-x64.zip","url":"https://updates.example.invalid/business/VPNRouterBusiness-update-v2.50.6-win-x64.zip","size":12345,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
```

The manifest is limited to16KiB/depth6 and the archive to512MiB, with a15minute
download deadline. The asset stays on the configured HTTPS authority without
query, user information or fragment. Versions must be canonical, stable and newer
than the running version. A missing/malformed source disables only updates.
Both Core and App must carry their own Business build identity; the App stamp
is `AssemblyMetadata("VPNRouterEdition", "Business")`. Staged files are bound to
the verified download in the same application session; restart requires redownload.

The Windows helper waits at most45seconds for its exact parent process to exit,
uses ordinary UAC when the installation directory requires elevation, verifies
file hashes and relaunches only after a successful copy. Replacement is atomic
per file, not across the entire package. On a mid-copy error it suppresses
relaunch and records a fixed failure code; retry the retained private installer
if recovery is needed. Provisioning/config remain outside the replaced files.
