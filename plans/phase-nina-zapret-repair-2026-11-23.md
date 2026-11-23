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
