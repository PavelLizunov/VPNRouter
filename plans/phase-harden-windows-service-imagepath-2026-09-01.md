# Phase: Harden Windows service ImagePath registration

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/harden-windows-service-imagepath`
Audit ID: `SU-3-6`

## 1. Intent & Invariants

- **What:** Persist the VPNRouter LocalSystem service `ImagePath` with literal quotes around only the executable path, and pass every `sc.exe` token through .NET's structured argument list.
- **Invariants:** the SCM value is exactly `"<absolute VPNRouter.Service.exe>" --service`; quoted current paths are idempotent; legacy unquoted paths self-heal; service name, account, dependencies, startup mode, description, failure recovery, and start/stop behavior remain unchanged; only inbox System32 `sc.exe` is launched; no merge/release/tag/deploy/install occurs.

## 2. Interface / Data Contract

```csharp
WindowsServiceCommand.FormatImagePath(exePath)
// => "\"C:\\Program Files\\VPNRouter\\VPNRouter.Service.exe\" --service"

ProcessStartInfo.FileName = <known System32 sc.exe>;
ProcessStartInfo.ArgumentList = { "create", "VPNRouter", "binPath=", imagePath, ... };

// sc qc BINARY_PATH_NAME parser preserves literal executable quotes.
```

## 3. Verification Checklist (Definition of Done)

- [ ] Empty/whitespace/embedded-quote paths fail before command construction.
- [ ] Relative and absolute paths normalize to one fully-qualified quoted executable plus `--service`.
- [ ] CLI service install and desktop install both use the shared formatter.
- [ ] Desktop self-heal preserves a correctly quoted path and repairs a legacy unquoted path.
- [ ] Every changed `sc.exe` invocation uses `ArgumentList`, never a concatenated `Arguments` command line.
- [ ] Both helpers resolve `sc.exe` from the Windows system directory rather than PATH/current directory.
- [ ] Dependencies, LocalSystem account, auto-start, display name, description, and failure recovery tokens remain pinned.
- [ ] Focused source/pure contracts and full exact-head CI pass.
- [ ] Independent correctness/security/test reviews have no surviving P0/P1.

## Risk / rollback

- Risk: incorrect quote layering can persist quote delimiters incorrectly and prevent service startup.
- Control: construct the persisted `ImagePath` as one data argument containing literal executable quotes, while .NET `ArgumentList` owns process-command-line escaping; pin the exact value and all `sc.exe` tokens without installing a service.
- Rollback: revert this task PR; no schema or migration exists. A legacy unquoted service remains self-healable by the repaired app after acceptance.

## Six gates

1. **Scope:** shared pure formatter, two Windows service helpers, deterministic contracts, ledger, and this brief only.
2. **Privilege boundary:** only System32 `sc.exe`; no concatenated executable/user path command line.
3. **SCM contract:** literal quotes surround only the executable and arguments stay outside.
4. **Compatibility:** service options and lifecycle commands are unchanged.
5. **Review:** independent SCM quoting, command execution, and test lenses; lead source-verifies findings.
6. **Handoff:** scoped commits, PR, exact-head green; owner alone decides merge/release.

## Primary sources

- [CreateService `lpBinaryPathName`](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-createservicea): a path containing spaces must be quoted and may include arguments.
- [.NET `ProcessStartInfo.ArgumentList`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0): arguments are escaped automatically and should be preferred over manually escaped `Arguments`.

## Outcome

Pending implementation and verification.
