# NINA repair on the Zapret page

- Branch `dsh/nina-zapret-repair-20261123`
- Base `d50cfb2fbfa15a6fd0e7e57a6e44a7b36eb8820b`
- Scope is the Windows Zapret page and the local headless NINA process.
- Risk is switching the network path during an active test. NINA owns the transaction and VPNRouter prevents simultaneous legacy Zapret operations.
- Reverting the VPNRouter change removes the entry point. The NINA stop command stops capture with the current Windows startup policy.

## Why

Users can repair a reported Discord Gateway connection from the existing Zapret page. One action runs the local probes, asks DeepSeek to choose each candidate and keeps a working profile automatically.

## How

Discover the repair capability from the Windows NINA installation in Program Files. Use the selected eligible VPNRouter subscription over a private stdin stream. Show each trial and its final result. Closing stdin requests rollback. Surface the gateway check as a specific result, since voice and media use other paths.

The preview requires a headless NINA installation and the matching vpnctl route. Ordinary VPNRouter settings and legacy Zapret profiles retain their existing ownership.

## Verification

1. Release build on the authorized Windows worker or CI.
2. Focused subprocess protocol tests for progress, cancellation and final results.
3. Record the setup and the verified platform and target.
4. Review the subscription transfer, process ownership and result handling.
5. Check successful repair, no improvement and cancellation on WINBRAT.
6. Check that the public MainWindowViewModel surface and legacy Zapret actions remain compatible.

## Review follow-up

Keep the original Zapret startup mode and recorded running state when migrating with NINA's compatible driver. The installer and the app must verify the same driver identity, and neither may modify the companion registration. Keep VPN connection and settings entry points idle during a NINA operation. If Setup cannot restore the VPNRouter service, show a localized warning and record the error code.

Verify with Windows migration and installer fixtures, ViewModel concurrency tests, and exact-head CI. The existing snapshot remains the rollback point. Parallel launches outside VPNRouter require coordination with the existing SCM entry point and remain a separate issue.

## Outcome

On WINBRAT, the first integrated snapshot `03aac53b` passed 60 focused Windows tests. The optional combined installer compiled with Inno Setup from the full VPNRouter package and a verified NINA archive. A local NINA trial on `1bf1ff5a` entered ready capture, rolled back after stdin was closed and restored Discord Gateway access when allowed to finish using a seeded test profile.

The test found that IP Helper assigned the same network GUID to the Ethernet and Tailscale interfaces. Profile reuse requires Windows Network List Manager's network ID for the selected route. The public picker route returned HTTP 404, so these checks did not exercise live DeepSeek.

The corrected NINA snapshot `3d7e4570` passed the seeded-cache trial and restored Discord Gateway access with the two control sites reachable. The final VPNRouter code snapshot `4c1f89dc` passed 81 focused tests on WINBRAT and the combined installer built from NINA `cefd8da1`. SHA-256 of this preview installer is `8fab5b1e64d3d8180db091460cefeb8a54fd17361234d6b6aa61e7629269def2`.

Independent review found that bundled setup failure could skip restoring the VPNRouter service. The optional setup now runs after restoration and reports its own failure separately. The preview preserves an older NINA directory while Windows keeps a handle to that driver's file. The live driver-handover fixture could not change the previous registration because Windows reported error 1072.

Follow-up in #729 applies the same driver compatibility rule before and during app migration and checks the companion file during installer preparation. The migration fixture covers matching and mismatching drivers and refuses writable access to the NINA registration. The installer fixture also covers active NINA capture. Five ViewModel cases exercise the VPN operation guard. Static checks confirmed the service start error path, its English and Russian messages and its order before optional NINA setup. The earlier preview precedes these changes.

Independent simultaneous starts from SCM and CLI remain unverified and can race before either process appears. The application gate covers operations started in VPNRouter. Live DeepSeek selection still requires the picker route to be deployed.
