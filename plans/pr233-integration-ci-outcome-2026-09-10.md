# PR233 initial integration CI

Snapshot bf67072f1c227708eeb99f20c6813e1a8df44ac6. Full Ubuntu test, Go Windows and fingerprint checks passed in34518382625/34518382050. Windows update package/native workflow34518382279 passed; detailed native-case receipts still need extraction before final acceptance. Duplicate manual run34518392401 cancellation requested to avoid repeating PR-triggered workload.

Windows characterization run failed one assertion: ReleaseSafetyBehaviorTests.UpdaterWorkflow_PowerShellBlocksParseInWindowsPowerShell51 line197 still expected9 extracted run blocks. Integration adds a tenth Cronet/native FakeIP block. Other439 passed,8 skipped. Actual workflow diff inspected: one new multiline PowerShell block. Corrective change pins10 instead of9 and retains parsing ALL blocks using Windows PowerShell5.1; does not remove safety coverage or alter product. Corrected exact-SHA CI pending.

Independent Linux worker at exact bf67072f: focused AntiCensorshipDns/VpnctlFakeIpMigration/NightDnsPrivacy/NightBaselineEndpoint/CustomConfigInjector/VpnctlPackagingCharacterization set passed133, skipped13, failed0. Command exit0; TRX in task checkout VPNRouter.Tests/TestResults/pr233-focused.trx. Skips include native/package cases unavailable on that host; not full native decoder evidence.

Worker clone /home/tester/vpnrouter-verification/pr233-bf67072f is task-owned, detached at exact SHA; retain until artifacts collected, then clean exact task outputs. No deployment/live VPN. macOS build script has shared /tmp outputs and prepends Homebrew PATH: do not execute unmodified on persistent worker without safe isolation/ownership checks. Android unsigned package and Linux/macOS package gates remain pending. No merge acceptance yet.
